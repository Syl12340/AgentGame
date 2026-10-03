#!/usr/bin/env python3
"""agents/explorer_agent.py -- agent/1 exploring agent for Facility Zero.

An external agent process driven by the Host over a stdin/stdout pipe:

  * one UTF-8 JSON object per line in, one per line out, flushed after every
    message;
  * ``hello`` is answered with ``ready`` (``protocol`` ``agent/1``);
  * every ``observation`` is answered with exactly one ``action`` carrying the
    observation's own ``request_id`` verbatim;
  * ``episode_end`` or stdin EOF ends the process normally (exit 0).

The agent consumes *only* the local observation the Host sends (position,
visible tiles, inventory, mission phase, last_result).  It never opens a
scenario file, the seed's map, credentials, a network socket or any other
repository file: the map is rebuilt from scratch out of partial observations.

Decision model
--------------
1. Map memory.  Every visible tile is merged into a dict keyed by (x, y)
   holding terrain, door state, item and the exit flag.  Negative facts learned
   from feedback are recorded too (``blocked`` with reason ``wall`` /
   ``out_of_bounds`` / ``closed_door``, ``missing_key`` / ``no_door`` on a
   failed ``interact``, ``nothing_to_pick_up`` on an empty ``pickup``), so a
   failed probe is turned into knowledge instead of being repeated in place.
2. Planning.  A* over the remembered, currently walkable tiles routes to an
   explicit goal (the remembered exit cell, a key, a core, a cell next to a
   closed door).  A goal that is only reachable through a remembered closed
   door is *not* routed to directly: the agent walks to the door instead.  When
   no goal is reachable, the nearest *frontier* -- an unseen tile touching a
   reachable walkable tile -- is explored; that is what uncovers unknown
   regions.
3. Task state machine.  Driven by inventory + mission phase + memory:
   find the key -> pick it up -> either explore for the core or open a door
   that actually unlocks a remembered-but-unreachable goal -> pick up the core
   -> walk back onto the remembered exit cell.  The last episode action is
   therefore a ``move`` onto the exit, which is the action the Core evaluates
   for success.  A door is only ever a target while the key is held, and only
   when opening it is a step towards a goal the agent has already seen
   (``_unlock_doors``): with no key in hand a door is merely a wall and the
   agent keeps looking for the key.
4. Replanning.  A plan is replayed while it stays valid.  Any ``blocked`` or
   ``no_effect`` feedback updates memory first and invalidates the plan, and the
   exact (state, action) pair that failed is remembered as forbidden.  A state
   is (position, inventory); it changes as soon as the agent moves or picks
   something up.  Planning therefore can never emit an action that already
   failed in the very same state: if the recomputed plan would start with that
   action, the agent falls through to exploration and finally to ``wait``, which
   makes the "same failing action twice in a row" livelock impossible.
5. Dead ends.  With no known goal, no frontier and no reachable tile the agent
   answers ``wait`` (legal, non-crashing) and only then nudges itself with an
   arbitrary legal step, instead of spinning or exiting.

Determinism: identical input sequences produce identical action sequences.
``--seed`` only breaks ties between equally good exploration targets; it never
influences map content.
"""

import argparse
import json
import signal
import sys

PROTOCOL = "agent/1"
AGENT_NAME = "explorer"
HELLO_ACTIONS = ["move", "pickup", "interact", "wait"]

DIRECTIONS = ("north", "east", "south", "west")
STEPS = {"north": (0, -1), "east": (1, 0), "south": (0, 1), "west": (-1, 0)}

WALL = "wall"
FLOOR = "floor"

# Consecutive deadlock waits before the agent nudges itself with a legal step.
MAX_CONSECUTIVE_WAITS = 3

# ``blocked`` / ``no_effect`` reasons that mean "this action cannot work here".
BLOCKED_REASONS = ("wall", "out_of_bounds", "closed_door")
INTERACT_FAILURES = ("missing_key", "no_door", "door_already_open")


# --------------------------------------------------------------------------- #
# protocol I/O
# --------------------------------------------------------------------------- #

def _configure_stdio():
    """Strict UTF-8 on the protocol streams; stderr must never be fatal."""
    for stream in (sys.stdin, sys.stdout):
        try:
            stream.reconfigure(encoding="utf-8", errors="strict")
        except (AttributeError, ValueError):
            pass
    try:
        sys.stderr.reconfigure(encoding="utf-8", errors="replace")
    except (AttributeError, ValueError):
        pass


def _emit(obj):
    """Write one protocol JSON line to stdout and flush immediately."""
    sys.stdout.write(json.dumps(obj, ensure_ascii=False) + "\n")
    sys.stdout.flush()


def _diagnostic(text):
    """Diagnostics go to stderr only; stdout stays pure protocol JSON."""
    try:
        sys.stderr.write("explorer: %s\n" % (text,))
        sys.stderr.flush()
    except Exception:  # pragma: no cover - diagnostics must never break a run
        pass


def move_action(direction):
    return {"type": "move", "direction": direction}


def interact_action(direction):
    return {"type": "interact", "direction": direction}


def pickup_action():
    return {"type": "pickup"}


def wait_action():
    return {"type": "wait"}


def action_key(action):
    """Hashable identity of an action, used to remember failures."""
    if not isinstance(action, dict):
        return None
    kind = action.get("type")
    if kind in ("move", "interact"):
        direction = action.get("direction")
        if direction not in STEPS:
            return None
        return (kind, direction)
    if kind in ("pickup", "wait"):
        return (kind,)
    return None


# --------------------------------------------------------------------------- #
# small geometry / value helpers
# --------------------------------------------------------------------------- #

def neighbors(pos):
    """The four orthogonal neighbours in the fixed north/east/south/west order."""
    x, y = pos
    for direction in DIRECTIONS:
        dx, dy = STEPS[direction]
        yield direction, (x + dx, y + dy)


def step(position, direction):
    dx, dy = STEPS[direction]
    return (position[0] + dx, position[1] + dy)


def as_position(node):
    """Extract an integer (x, y) from a wire object, else None."""
    if not isinstance(node, dict):
        return None
    x, y = node.get("x"), node.get("y")
    if isinstance(x, int) and isinstance(y, int):
        return (x, y)
    return None


def manhattan(a, b):
    return abs(a[0] - b[0]) + abs(a[1] - b[1])


def tie_break(position, seed):
    """Stable per-seed ordering, used only to break equal-cost ties."""
    value = (position[0] * 73856093) ^ (position[1] * 19349663) ^ (seed * 2654435761)
    return value & 0xFFFFFFFF


# --------------------------------------------------------------------------- #
# map memory
# --------------------------------------------------------------------------- #

def _new_record(terrain=FLOOR, is_door=False, door_open=False, item=None, is_exit=False):
    return {
        "terrain": terrain,
        "is_door": is_door,
        "door_open": door_open,
        "item": item,
        "is_exit": is_exit,
    }


class Memory:
    """Partial map rebuilt from local observations only."""

    def __init__(self):
        self.tiles = {}        # (x, y) -> tile record
        self.visit_count = {}  # (x, y) -> times occupied
        # Proven false by feedback; cleared when the inventory changes, because
        # that is the only thing that can make them true again.
        self.blocked_moves = set()     # (from, direction) -> wall / closed door
        self.bad_interacts = set()     # (from, direction) -> missing_key / no_door
        self.empty_pickups = set()     # (x, y) -> nothing_to_pick_up

    # -- merging -------------------------------------------------------------

    def absorb_tiles(self, tiles, picked=()):
        """Merge visible tiles; ``picked`` filters items the agent already holds.

        The Host is authoritative, but a just-taken item can still be visible on
        the very next observation.  Keeping it would make every later plan route
        back and pick up an empty cell, so the agent drops it as soon as a
        successful pickup (or the inventory) confirms it.
        """
        if not isinstance(tiles, list):
            return
        for tile in tiles:
            if not isinstance(tile, dict):
                continue
            position = as_position(tile)
            if position is None:
                continue
            terrain = tile.get("terrain")
            terrain = terrain if terrain in (WALL, FLOOR) else FLOOR
            record = self.tiles.get(position)
            if record is None:
                record = _new_record(terrain=terrain)
                self.tiles[position] = record
            record["terrain"] = terrain
            if terrain == WALL:
                # A wall is never a door, an item or an exit.
                record["is_door"] = False
                record["door_open"] = False
                record["item"] = None
                continue
            record["is_exit"] = bool(tile.get("is_exit", False))
            door_open = tile.get("door_open")
            if isinstance(door_open, bool):
                record["is_door"] = True
                record["door_open"] = door_open
            elif record["is_door"]:
                # Visible again without a door flag: the door is gone.
                record["is_door"] = False
                record["door_open"] = False
            item = tile.get("item")
            if item in ("key", "core") and item not in picked:
                record["item"] = item
            else:
                # A visible tile whose item was taken is authoritative.
                record["item"] = None

    def absorb_feedback(self, position, last_result, last_action):
        """Fold last_result into memory; must run before any replanning."""
        if not isinstance(last_result, dict):
            return
        status = last_result.get("status")
        reason = last_result.get("reason")
        action = action_key(last_action)
        if action is None:
            return
        kind = action[0]

        if status == "blocked" and reason in BLOCKED_REASONS and kind == "move":
            origin, direction = action[1], action[2]
            target = step(origin, direction)
            self.blocked_moves.add((origin, direction))
            record = self.tiles.get(target)
            if reason == "closed_door":
                # A move that hits a closed door proves the door is there.
                if record is None or record["terrain"] != WALL:
                    record = self.tiles.setdefault(target, _new_record())
                    record["terrain"] = FLOOR
                    record["is_door"] = True
                    record["door_open"] = False
            else:
                # wall / out_of_bounds: the cell cannot be entered.
                if record is None:
                    self.tiles[target] = _new_record(terrain=WALL)
        elif kind == "interact" and status in ("blocked", "no_effect"):
            if reason in INTERACT_FAILURES:
                origin, direction = action[1], action[2]
                target = step(origin, direction)
                # Remember the direction as not interactable until the
                # inventory changes: with no key the door cannot be opened, and
                # retrying it every turn is exactly the livelock to avoid.
                self.bad_interacts.add((origin, direction))
                if reason == "no_door":
                    record = self.tiles.get(target)
                    if record is not None and record["terrain"] != WALL:
                        # The Host says there is no door here: drop the ghost.
                        if record["is_door"] and not record["door_open"]:
                            record["is_door"] = False
                            record["door_open"] = False
        elif kind == "pickup" and status == "no_effect":
            self.empty_pickups.add(position)
            record = self.tiles.get(position)
            if record is not None:
                record["item"] = None

    def clear_feedback_facts(self):
        """Inventory changed: previously impossible actions may work now."""
        self.blocked_moves.clear()
        self.bad_interacts.clear()
        self.empty_pickups.clear()

    def bump(self, position):
        self.visit_count[position] = self.visit_count.get(position, 0) + 1

    # -- queries -------------------------------------------------------------

    def record(self, position):
        return self.tiles.get(position)

    def is_door(self, position):
        record = self.tiles.get(position)
        return record is not None and record["is_door"]

    def closed_door(self, position):
        record = self.tiles.get(position)
        return bool(record is not None and record["is_door"] and not record["door_open"])

    def walkable(self, position, use_key=False):
        """Traversal predicate for planning.

        ``use_key=True`` answers a hypothetical question: "would this cell be
        passable if the agent could open a closed door on contact?".  It is used
        to recognise goals that are still locked behind a door
        (``blocked_goal_neighbors``).  Every route that will actually be walked
        uses ``use_key=False``, because a closed door stops a ``move`` until it
        has been opened with ``interact``.  Unknown tiles are never walkable:
        they are only entered as a deliberate one-step probe from an adjacent
        cell.
        """
        record = self.tiles.get(position)
        if record is None or record["terrain"] == WALL:
            return False
        if not record["is_door"]:
            return True
        if record["door_open"]:
            return True
        return bool(use_key)

    def move_allowed(self, origin, direction):
        return (origin, direction) not in self.blocked_moves

    def interact_allowed(self, origin, direction):
        return (origin, direction) not in self.bad_interacts

    def pickup_allowed(self, position):
        return position not in self.empty_pickups

    def exit_positions(self):
        return sorted(p for p, r in self.tiles.items() if r["is_exit"])

    def item_positions(self, kind):
        return sorted(p for p, r in self.tiles.items() if r["item"] == kind)

    def closed_doors(self):
        return sorted(p for p, r in self.tiles.items() if r["is_door"] and not r["door_open"])


# --------------------------------------------------------------------------- #
# planning primitives
# --------------------------------------------------------------------------- #

def a_star(start, goal, memory, use_key=False, virtual_open=()):
    """A* over remembered tiles; returns a list of action dicts or None."""
    if start == goal:
        return []
    if not walkable_with(goal, memory, use_key, virtual_open):
        return None
    open_set = {start}
    came_from = {}
    g_score = {start: 0}
    f_score = {start: manhattan(start, goal)}
    while open_set:
        current = min(open_set, key=lambda node: (f_score.get(node, 1 << 30), node[0], node[1]))
        if current == goal:
            return _reconstruct(came_from, current)
        open_set.discard(current)
        for direction, nxt in neighbors(current):
            if not walkable_with(nxt, memory, use_key, virtual_open):
                continue
            tentative = g_score[current] + 1
            if tentative < g_score.get(nxt, 1 << 30):
                came_from[nxt] = (current, direction)
                g_score[nxt] = tentative
                f_score[nxt] = tentative + manhattan(nxt, goal)
                open_set.add(nxt)
    return None


def walkable_with(position, memory, use_key, virtual_open=()):
    if virtual_open and position in virtual_open:
        return True
    return memory.walkable(position, use_key)


def _reconstruct(came_from, current):
    path = []
    while current in came_from:
        previous, direction = came_from[current]
        path.append(move_action(direction))
        current = previous
    path.reverse()
    return path


def replay(start, actions, memory, use_key=False):
    """Simulate an action list from ``start`` without mutating memory.

    Returns ``(position, executable_prefix)``.  Simulation stops before the
    first action whose precondition does not hold, e.g. a move into a closed
    door (which must be opened with ``interact`` first).
    """
    position = start
    doors = {}
    prefix = []
    for action in actions:
        kind = action["type"]
        if kind == "move":
            target = step(position, action["direction"])
            if not _walkable_sim(target, memory, doors, use_key):
                break
            position = target
        elif kind == "interact":
            target = step(position, action["direction"])
            if not _closed_door_sim(target, memory, doors):
                break
            doors[target] = True
        elif kind == "pickup":
            pass  # always a legal attempt; an empty tile is just no_effect
        elif kind != "wait":
            break
        prefix.append(action)
    return position, prefix


def _walkable_sim(position, memory, doors, use_key):
    if doors.get(position):
        return True
    record = memory.record(position)
    if record is None or record["terrain"] == WALL:
        return False
    if not record["is_door"]:
        return True
    return bool(record["door_open"] or use_key)


def _closed_door_sim(position, memory, doors):
    if doors.get(position):
        return False
    if not memory.is_door(position):
        return False
    record = memory.record(position)
    return bool(record is not None and not record["door_open"])


def open_door_action(position, memory):
    """``interact`` towards an adjacent closed door, or None."""
    for direction, target in neighbors(position):
        if memory.closed_door(target) and memory.interact_allowed(position, direction):
            return interact_action(direction)
    return None


def blocked_goal_neighbors(memory):
    """Seen items / exits that are still not walkable (i.e. behind a door)."""
    goals = set()
    for kind in ("key", "core"):
        for position in memory.item_positions(kind):
            if not memory.walkable(position, use_key=False):
                goals.add(position)
    for position in memory.exit_positions():
        if not memory.walkable(position, use_key=False):
            goals.add(position)
    return goals


def reachable_positions(memory, start, virtual_open=()):
    """Positions reachable from ``start`` with ``virtual_open`` doors open."""
    seen = set()
    if not walkable_with(start, memory, False, virtual_open):
        return seen
    seen.add(start)
    queue = [start]
    head = 0
    while head < len(queue):
        current = queue[head]
        head += 1
        for _, nxt in neighbors(current):
            if nxt in seen:
                continue
            if not walkable_with(nxt, memory, False, virtual_open):
                continue
            seen.add(nxt)
            queue.append(nxt)
    return seen


def _opens_frontier(memory, newly):
    """How much unknown space this door opens onto (0 = it hides nothing).

    ``newly`` is the region that only becomes reachable once the door is open.
    Every unseen tile touching it is a place worth being, so the count is a
    cheap "how much is behind this door?" score.  A door into a fully explored
    pocket scores 0: opening it would reveal nothing and is not worth an
    ``interact``.
    """
    score = 0
    for position in newly:
        for _, nxt in neighbors(position):
            if nxt not in memory.tiles:
                score += 1
    return score


def sealed_penalty(position, memory):
    """Penalty for hugging known walls/edges: keeps probes near open space."""
    penalty = 0
    for _, nxt in neighbors(position):
        record = memory.tiles.get(nxt)
        if record is None or record["terrain"] == WALL:
            penalty += 1
    return penalty


def pick_target(candidates, costs, memory, goal_hint, seed):
    """Deterministically choose the cheapest candidate."""
    best_key = None
    best = None
    for position in sorted(candidates):
        cost = costs.get(position)
        if cost is None:
            continue
        hint_bonus = 0
        if goal_hint is not None:
            hint_bonus = manhattan(position, goal_hint) // 4
        key = (
            cost + 2 * sealed_penalty(position, memory) + hint_bonus,
            memory.visit_count.get(position, 0),
            tie_break(position, seed),
        )
        if best_key is None or key < best_key:
            best_key = key
            best = position
    return best


# --------------------------------------------------------------------------- #
# the planner / state machine
# --------------------------------------------------------------------------- #

class Explorer:
    """Map memory + planner + task state machine for one episode."""

    def __init__(self, seed=0):
        self.memory = Memory()
        self.seed = seed
        self.plan = []
        self.last_position = None
        self.last_move = None       # (from, direction, to) of the previous move
        self.last_action = None     # the previous action dict, verbatim
        self.failed = {}            # (fingerprint, action_key) -> failure reason
        self.has_key = False
        self.has_core = False
        self.picked_key = False
        self.picked_core = False
        self.mission = None
        self.is_done = False
        self.picked = set()           # item kinds already taken off the ground
        self.exit_candidates = set()  # every cell ever seen flagged is_exit
        self.consecutive_waits = 0

    # -- observation ingestion ----------------------------------------------

    def observe(self, observation, last_result):
        position = as_position(observation.get("position"))
        if position is None:
            position = self.last_position or (0, 0)
        self.memory.bump(position)

        inventory = observation.get("inventory")
        inventory = inventory if isinstance(inventory, list) else []
        previous_inventory = tuple(sorted(self.picked)) if self.picked else ()
        mission = observation.get("mission")
        if "key" in inventory:
            self.picked_key = True
        if "core" in inventory or mission == "return_to_exit":
            # The Core only enters return_to_exit with the core in the inventory.
            self.picked_core = True
        # A *successful pickup* is authoritative even if the item is still listed
        # on a visible tile in this same observation.  Only a pickup can prove
        # one: a successful move onto the item's tile says nothing about the
        # inventory, and treating it as a pickup would erase a key the agent
        # never took.
        if isinstance(last_result, dict) and last_result.get("status") == "applied" \
                and self._last_action_type() == "pickup":
            previous = self.memory.record(position)
            if previous is not None and previous["item"] in ("key", "core"):
                if previous["item"] == "key":
                    self.picked_key = True
                else:
                    self.picked_core = True
        self.picked = {kind for kind, flag in
                       (("key", self.picked_key), ("core", self.picked_core)) if flag}
        self.has_key = self.has_key or self.picked_key
        self.has_core = self.has_core or self.picked_core

        # Feedback is folded into memory *before* anything is planned, and any
        # failure is remembered against the state it happened in.
        self.memory.absorb_feedback(position, last_result, self.last_action)
        self._remember_failure(position, last_result)
        if tuple(sorted(self.picked)) != previous_inventory:
            # A key in hand makes a previously refused door openable again.
            self.memory.clear_feedback_facts()
            self.failed.clear()

        self.memory.absorb_tiles(observation.get("tiles"), picked=self.picked)
        # The picked item is gone from the ground even while it stays visible.
        for tile_position, record in self.memory.tiles.items():
            if record["item"] in self.picked:
                record["item"] = None

        for candidate in self.memory.exit_positions():
            self.exit_candidates.add(candidate)

        if isinstance(mission, str):
            self.mission = mission
        if self.mission == "succeeded":
            self.is_done = True
        self.last_position = position
        self.last_move = None
        self.last_action = None

    def _remember_failure(self, position, last_result):
        """Persist "this action failed in this state" so it is never repeated."""
        if not isinstance(last_result, dict):
            return
        status = last_result.get("status")
        if status not in ("blocked", "no_effect"):
            return
        key = action_key(self.last_action)
        if key is None:
            return
        self.failed[(self._fingerprint(position), key)] = last_result.get("reason") or status

    def _last_action_type(self):
        if isinstance(self.last_action, dict):
            return self.last_action.get("type")
        return None

    def _fingerprint(self, position):
        """The state a failed action is remembered against.

        Position and inventory are exactly what a ``blocked``/``no_effect``
        action cannot change, so a repeated attempt in the same situation is
        recognised.  Door state is refreshed from the authoritative observation
        every tick, and an inventory change clears all remembered failures.
        """
        return (position, tuple(sorted(self.picked)))

    def _action_allowed(self, position, action, allow_unseen=False):
        """Gate every action a plan may start with.

        ``allow_unseen`` is set for a frontier probe: its whole purpose is to
        step into a tile that is *not* remembered yet, so the walkability check
        is replaced by "the Host has not already refused this step".
        """
        if not isinstance(action, dict):
            return False
        key = action_key(action)
        if key is None:
            return False
        if (self._fingerprint(position), key) in self.failed:
            return False
        kind = key[0]
        if kind == "move":
            direction = key[1]
            target = step(position, direction)
            if not self.memory.move_allowed(position, direction):
                return False
            if allow_unseen and target not in self.memory.tiles:
                return True
            return self.memory.walkable(target, use_key=False)
        if kind == "interact":
            direction = key[1]
            target = step(position, direction)
            if not self.memory.interact_allowed(position, direction):
                return False
            return self.memory.closed_door(target)
        if kind == "pickup":
            return self.memory.pickup_allowed(position)
        return True  # ``wait`` is always legal

    # -- graph helpers -------------------------------------------------------

    def _distances(self, start, use_key=False):
        """BFS distances from ``start`` over walkable remembered tiles."""
        walkable = self.memory.walkable
        distances = {start: 0}
        queue = [start]
        head = 0
        while head < len(queue):
            current = queue[head]
            head += 1
            step_cost = distances[current] + 1
            for _, nxt in neighbors(current):
                if nxt in distances or not walkable(nxt, use_key):
                    continue
                distances[nxt] = step_cost
                queue.append(nxt)
        return distances

    def _frontiers(self, distances):
        """Unseen tiles touching a reachable walkable tile, with their cost."""
        frontier = {}
        for position in distances:
            for _, nxt in neighbors(position):
                if nxt in self.memory.tiles:
                    continue
                cost = distances[position] + 1
                if frontier.get(nxt, 1 << 30) > cost:
                    frontier[nxt] = cost
        return frontier

    def _route(self, start, goal, use_key=False):
        """Walkable move route from ``start`` to ``goal`` (possibly empty).

        ``use_key=False`` by default: a route may never walk through a closed
        door, it has to stop next to it and let the door plan open it first.
        """
        if goal is None:
            return None
        if start == goal:
            return []
        route = a_star(start, goal, self.memory, use_key)
        if route is None:
            return None
        _, prefix = replay(start, route, self.memory, use_key)
        if start != goal and not prefix:
            return None
        return prefix

    def _goal_plan(self, start, goal, kind):
        """Route to ``goal`` and perform the action the goal calls for."""
        if goal is None:
            return None
        route = self._route(start, goal)
        if route is None:
            return None
        if kind == "pickup":
            if start == goal:
                return [pickup_action()]
            if not self.memory.pickup_allowed(goal):
                return None
            return route + [pickup_action()]
        return route

    def _unlock_doors(self, start):
        """Sorted doors worth opening, most purposeful first.

        A door is only ever a target while the key is held (only then does
        ``interact`` do anything).  Two kinds are worth opening:

        * a door through which a goal the agent has already seen (a remembered
          ``key`` / ``core`` / ``exit``) comes within reach, and
        * a door with unknown space behind it, best first: ``_opens_frontier``
          counts the unseen tiles the door opens onto, so the agent walks to the
          most promising door instead of wandering the region it already knows.

        Anything else is left alone.  In particular a door that hides nothing
        the agent can still want is *not* hammered, which is what kept the agent
        in a ``missing_key`` livelock before.
        """
        if not self.has_key:
            return []
        doors = self.memory.closed_doors()
        if not doors:
            return []
        goals = blocked_goal_neighbors(self.memory)
        plain = reachable_positions(self.memory, start)
        unlocked_goal = []
        gates_frontier = []
        for door in doors:
            open_reach = reachable_positions(self.memory, start, virtual_open=(door,))
            newly = open_reach - plain
            if goals and any(goal in open_reach for goal in goals):
                unlocked_goal.append(door)
                continue
            score = _opens_frontier(self.memory, newly)
            if score:
                gates_frontier.append((score, door))
        gates_frontier.sort(key=lambda item: (-item[0], item[1]))
        return unlocked_goal + [door for _, door in gates_frontier]

    def _door_plan(self, start):
        """Walk next to a door that unlocks a known goal or an unseen region."""
        for door in self._unlock_doors(start):
            direction = _direction_to(start, door)
            if direction is not None and not self._action_allowed(start, interact_action(direction)):
                direction = None
            if direction is not None:
                return [interact_action(direction)]
            for _, approach in neighbors(door):
                if not self.memory.walkable(approach, use_key=False):
                    continue
                approach_direction = _direction_to(approach, door)
                if approach_direction is None:
                    continue
                action = interact_action(approach_direction)
                if not self._action_allowed(approach, action):
                    continue
                route = self._route(start, approach)
                if route is None:
                    continue
                return route + [action]
        return None

    def _explore_plan(self, start, goal_hint):
        """Nearest frontier; prefers frontiers next to a known blocked goal.

        Exploration uses exactly the walkable relation a ``move`` obeys (closed
        doors are not walkable), so a probe route can never be emitted through a
        closed door.  A frontier that hides behind a door is still found: the
        frontier next to that door is reachable, and probing it reveals the door.
        """
        distances = self._distances(start, use_key=False)
        frontier = self._frontiers(distances)
        if not frontier:
            return None
        blocked_goals = blocked_goal_neighbors(self.memory)
        near_goal = [
            position for position in frontier
            if any(nxt in blocked_goals for _, nxt in neighbors(position))
        ]
        candidates = near_goal if near_goal else list(frontier.keys())
        target = pick_target(candidates, frontier, self.memory, goal_hint, self.seed)
        if target is None and near_goal:
            target = pick_target(frontier.keys(), frontier, self.memory, goal_hint, self.seed)
        if target is None:
            return None
        if target == start:
            return []
        for _, approach in _approaches(target):
            if not self.memory.walkable(approach, use_key=False):
                continue
            route = self._route(start, approach, use_key=False)
            if route is None:
                continue
            probe = _probe_move(approach, target)
            if not self._action_allowed(approach, probe, allow_unseen=True):
                continue
            simulated, prefix = replay(start, route, self.memory, use_key=False)
            if simulated != approach or not prefix:
                continue
            return route + [probe]
        return None

    def _hint_goal(self):
        """Cheap directional bias for exploration (None = no bias)."""
        if self.has_core:
            return self._home_exit()
        if not self.has_key:
            return None
        cores = self.memory.item_positions("core")
        if cores:
            return cores[0]
        return None

    def _home_exit(self):
        if self.exit_candidates:
            return sorted(self.exit_candidates)[0]
        exits = self.memory.exit_positions()
        return exits[0] if exits else None

    # -- decision -----------------------------------------------------------

    def decide(self, position):
        """Return the next action dict, or None when the episode is over."""
        if self.is_done:
            return None

        for plan in self._plan_sources(position):
            if not plan:
                continue
            _, prefix = replay(position, plan, self.memory, use_key=False)
            if not prefix:
                continue
            action = prefix[0]
            if not self._action_allowed(position, action):
                # The very action this plan starts with is known to fail here.
                continue
            self.plan = []
            self._note_action(position, action)
            return action
        return self._stall_action(position)

    def _plan_sources(self, position):
        """In-progress plan first, then freshly computed goal plans."""
        if self.plan:
            yield self.plan
        for plan in self._candidate_plans(position):
            yield plan

    def _goal_with_gate(self, position, goal, kind):
        """The plan that reaches ``goal``: straight there, or through a door.

        When the only route to a remembered goal runs through a closed door,
        walking "towards" it would just stop on the door's own tile and the next
        replan would produce the same step; opening the door is emitted instead.
        A route that works without touching a closed door still wins, so the
        extra gate never costs a detour on maps that do not need it.
        """
        if goal is None:
            return
        if goal in self._gated_goals(position):
            plan = self._door_plan(position)
            if plan:
                yield plan
        plan = self._goal_plan(position, goal, kind)
        if plan:
            yield plan
        plan = self._door_plan(position)
        if plan:
            yield plan

    def _gated_goals(self, position):
        """Remembered key/core/exit goals reachable only through a closed door."""
        gated = set()
        for kind in ("key", "core"):
            for goal in self.memory.item_positions(kind):
                if not self.memory.walkable(goal, use_key=False):
                    gated.add(goal)
        for goal in self.memory.exit_positions():
            if self.memory.walkable(goal, use_key=True) \
                    and not self.memory.walkable(goal, use_key=False):
                gated.add(goal)
        return gated

    def _candidate_plans(self, position):
        """Goal plans in task order; the first feasible one wins."""
        exit_position = self._home_exit()
        if self.has_core:
            yield from self._goal_with_gate(position, exit_position, "walk")
            yield self._explore_plan(position, self._hint_goal())
            return
        if not self.has_key:
            # No key yet: pick a seen key up if it is reachable, otherwise look
            # for it.  Opening doors only ever works with a key in hand, so
            # until then the frontier probe is what reveals the way on -- and
            # repeatedly interacting with a locked door is exactly the livelock
            # this ordering removes.
            for goal in self.memory.item_positions("key"):
                yield from self._goal_with_gate(position, goal, "pickup")
            yield self._explore_plan(position, self._hint_goal())
            yield self._door_plan(position)
            return
        yield self._door_plan(position)
        for goal in self.memory.item_positions("core"):
            yield from self._goal_with_gate(position, goal, "pickup")
        yield self._explore_plan(position, self._hint_goal())

    def _note_action(self, position, action):
        self.last_action = action
        if action["type"] == "move":
            self.last_move = (position, action["direction"], step(position, action["direction"]))
        else:
            self.last_move = None

    def _stall_action(self, position):
        """No goal, no frontier, no route: wait rather than crash or spin."""
        self.consecutive_waits += 1
        if self.consecutive_waits < MAX_CONSECUTIVE_WAITS:
            return wait_action()
        self.consecutive_waits = 0
        for direction, target in neighbors(position):
            if not self.memory.walkable(target, use_key=False):
                continue
            action = move_action(direction)
            if not self._action_allowed(position, action):
                continue
            self._note_action(position, action)
            return action
        return wait_action()


def _approaches(target):
    """Adjacent cells, in the canonical direction order of ``neighbors``."""
    return list(neighbors(target))


def _direction_to(origin, target):
    """The direction that steps from ``origin`` onto ``target``, else None."""
    for direction, nxt in neighbors(origin):
        if nxt == target:
            return direction
    return None


def _probe_move(approach, target):
    """The move that steps from ``approach`` into the unseen cell ``target``."""
    for direction, nxt in neighbors(approach):
        if nxt == target:
            return move_action(direction)
    raise ValueError("probe target is not adjacent to its approach cell")


# --------------------------------------------------------------------------- #
# host loop
# --------------------------------------------------------------------------- #

def run_host_loop(explorer):
    """Consume host JSONL until episode_end or EOF; returns the exit code."""
    started = False
    for line in sys.stdin:
        text = line.rstrip("\r\n")
        if not text.strip():
            continue
        try:
            message = json.loads(text)
        except (json.JSONDecodeError, ValueError):
            _diagnostic("invalid host JSON; stopping with exit 1")
            return 1
        if not isinstance(message, dict):
            _diagnostic("expected a host object; stopping with exit 1")
            return 1

        msg_type = message.get("type")
        if msg_type == "hello":
            if started:
                _diagnostic("duplicate hello ignored")
                continue
            if message.get("protocol") != PROTOCOL or message.get("actions") != HELLO_ACTIONS:
                _diagnostic("unsupported hello; stopping with exit 1")
                return 1
            _emit({"type": "ready", "protocol": PROTOCOL, "name": AGENT_NAME})
            started = True
        elif msg_type == "observation":
            if not started:
                _diagnostic("observation before handshake; stopping with exit 1")
                return 1
            request_id = message.get("request_id")
            if not isinstance(request_id, str) or not request_id:
                _diagnostic("invalid request_id; stopping with exit 1")
                return 1
            observation = message.get("observation")
            if not isinstance(observation, dict):
                observation = {}
            explorer.observe(observation, observation.get("last_result"))
            position = as_position(observation.get("position")) or explorer.last_position or (0, 0)
            action = explorer.decide(position)
            if action is None:
                action = wait_action()
            _emit({"type": "action", "request_id": request_id, "action": action})
        elif msg_type == "episode_end":
            observation = message.get("observation")
            if isinstance(observation, dict):
                explorer.observe(observation, observation.get("last_result"))
            _diagnostic("episode_end received; exiting 0")
            return 0
        else:
            _diagnostic("unexpected host message %r; stopping with exit 1" % (msg_type,))
            return 1
    _diagnostic("stdin EOF; exiting 0")
    return 0


def main(argv=None):
    _configure_stdio()
    parser = argparse.ArgumentParser(
        prog="explorer_agent",
        description="agent/1 exploring agent over stdin JSONL.",
    )
    parser.add_argument("--seed", type=int, default=0,
                        help="tie-break seed for equally good exploration targets (default 0)")
    args = parser.parse_args(argv)
    explorer = Explorer(seed=args.seed)
    return run_host_loop(explorer)


def _handle_termination(_signum, _frame):
    """SIGTERM / SIGINT: leave quietly with a success status."""
    raise SystemExit(0)


def _install_signal_handlers():
    for name in ("SIGTERM", "SIGINT"):
        number = getattr(signal, name, None)
        if number is None:
            continue
        try:
            signal.signal(number, _handle_termination)
        except (ValueError, OSError, RuntimeError):  # pragma: no cover
            pass


if __name__ == "__main__":
    _install_signal_handlers()
    try:
        sys.exit(main())
    except BrokenPipeError:
        try:
            sys.stdout.close()
        finally:
            sys.exit(0)
    except KeyboardInterrupt:
        sys.exit(130)
