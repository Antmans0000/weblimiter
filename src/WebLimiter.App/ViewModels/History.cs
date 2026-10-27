namespace WebLimiter.App.ViewModels;

/// <summary>Fixed-size ring of the last N per-second rates, for the traffic graph.</summary>
public sealed class History
{
    public const int Length = 120;

    private readonly double[] _down = new double[Length];
    private readonly double[] _up = new double[Length];
    private int _head;

    public void Push(double down, double up)
    {
        _down[_head] = down;
        _up[_head] = up;
        _head = (_head + 1) % Length;
    }

    /// <summary>Value i seconds ago ordered oldest (0) to newest (Length-1).</summary>
    public double Down(int i) => _down[(_head + i) % Length];
    public double Up(int i) => _up[(_head + i) % Length];
}
