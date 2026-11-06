using System;
using System.Linq;
namespace GearHub.Ui;

/// <summary>
/// A damped spring, the motion of the ilha's pill: a damping ratio of ~0.68 overshoots a little and settles, the way
/// the Dynamic Island grows and shrinks.
/// </summary>
public sealed class Spring
{
    public double Value, Velocity, Target;
    public double Stiffness = 300, Damping = 23.5;

    public Spring(double value) => Value = Target = value;

    public bool Settled => Math.Abs(Value - Target) < 0.05 && Math.Abs(Velocity) < 0.05;

    public void Step(double dt)
    {
        // fixed substeps keep it stable when a frame comes late
        int n = Math.Max(1, (int)Math.Ceiling(dt / (1.0 / 240)));
        double h = dt / n;
        for (int i = 0; i < n; i++)
        {
            double accel = -Stiffness * (Value - Target) - Damping * Velocity;
            Velocity += accel * h;
            Value += Velocity * h;
        }
        if (Settled) { Value = Target; Velocity = 0; }
    }

    public void Snap()
    {
        Value = Target;
        Velocity = 0;
    }
}
