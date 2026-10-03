namespace AgentGame.Core;

/// <summary>Integer supercover rays; tied corner crossings test both side cells first.</summary>
internal static class Visibility
{
    public static bool HasLineOfSight(Scenario scene, Position origin, Position target, bool doorOpen)
    {
        if (origin == target) return true;
        int nx = Math.Abs(target.X - origin.X), ny = Math.Abs(target.Y - origin.Y);
        int sx = Math.Sign(target.X - origin.X), sy = Math.Sign(target.Y - origin.Y);
        int x = origin.X, y = origin.Y, ix = 0, iy = 0;
        while (ix < nx || iy < ny)
        {
            int decision = (1 + 2 * ix) * ny - (1 + 2 * iy) * nx;
            if (decision == 0)
            {
                if (Opaque(new(x + sx, y)) || Opaque(new(x, y + sy))) return false;
                x += sx; y += sy; ix++; iy++;
            }
            else if (decision < 0) { x += sx; ix++; }
            else { y += sy; iy++; }
            var current = new Position(x, y);
            if (current == target) return true;
            if (Opaque(current)) return false;
        }
        return true;

        bool Opaque(Position p) => !scene.Contains(p) || scene.At(p) == Terrain.Wall || (p == scene.Door && !doorOpen);
    }
}