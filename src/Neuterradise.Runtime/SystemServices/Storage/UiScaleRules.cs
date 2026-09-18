namespace Neuterradise.App.SystemServices.Storage;

/// <summary>
/// The one definition of the app-wide UI scale contract: 60%–125%, default 100%, 5% steps.
/// Persisted values are normalized through here so a hand-edited config can never push the shell
/// outside the supported range. ScaleHost measures content against the inverse scale, so the lower
/// bound remains a layout reflow rather than a clipped fixed-size transform.
/// </summary>
public static class UiScaleRules
{
    public const double Minimum = 0.60;

    public const double Maximum = 1.25;

    public const double Default = 1.00;

    public const double Step = 0.05;

    public static double Normalize(double value)
    {
        if (!double.IsFinite(value))
        {
            return Default;
        }

        var stepped = Math.Round(value / Step, MidpointRounding.AwayFromZero) * Step;
        return Math.Round(Math.Clamp(stepped, Minimum, Maximum), 2);
    }

    public static int ToPercent(double scale) => (int)Math.Round(scale * 100, MidpointRounding.AwayFromZero);
}
