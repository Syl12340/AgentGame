namespace AgentGame.Protocol;

/// <summary>Absolute cardinal directions: north = (0,-1), east = (1,0), south = (0,1), west = (-1,0).</summary>
public enum DirectionDto { North, East, South, West }

/// <summary>The four legal Agent actions. move/interact require a direction; pickup/wait must not carry one.</summary>
public enum ActionTypeDto { Move, Pickup, Interact, Wait }

/// <summary>Feedback status returned by the rules. Unknown protocol/transport states are not game feedback.</summary>
public enum ActionStatusDto { Applied, Blocked, NoEffect }

public enum TerrainDto { Floor, Wall }

public enum ItemKindDto { Key, Core }

/// <summary>Mission phase derived by the rules from inventory/door/core state.</summary>
public enum MissionPhaseDto { FindKey, OpenDoor, FindCore, ReturnToExit, Succeeded }

public enum EpisodeKindDto { Success, TurnLimit }

/// <summary>Observer peripheral run state, published in the ordered stream (may change without a tick).</summary>
public enum AgentStatusDto { Waiting, ActionReceived, Stopped, ResyncRequired, Errored }