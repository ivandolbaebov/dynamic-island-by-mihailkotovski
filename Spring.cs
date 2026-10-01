namespace DynamicIsland;

/// <summary>Damped spring used for every island motion (size, radius, scale, offset).</summary>
sealed class Spring
{
    const double Step = 1.0 / 240;

    public double Value;
    public double Velocity;
    public double Target;
    public double Stiffness = 300;
    public double Damping = 24;

    public Spring(double value)
    {
        Value = Target = value;
    }

    public void Tune(double stiffness, double damping)
    {
        Stiffness = stiffness;
        Damping = damping;
    }

    /// <summary>Advances the simulation; returns false once the spring is at rest.</summary>
    public bool Advance(double dt)
    {
        while (dt > 0)
        {
            double h = Math.Min(Step, dt);
            double accel = -Stiffness * (Value - Target) - Damping * Velocity;
            Velocity += accel * h;
            Value += Velocity * h;
            dt -= h;
        }

        if (Math.Abs(Value - Target) < 0.005 && Math.Abs(Velocity) < 0.05)
        {
            Value = Target;
            Velocity = 0;
            return false;
        }
        return true;
    }
}
