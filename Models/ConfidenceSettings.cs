namespace NetworkMonitor.ML.Model;

// Host/API values use percentages. Accept historical TimesFM fractions below 1
// at the backend boundary so existing configurations keep their meaning.
internal static class ConfidenceSettings
{
    internal static double ToPercent(double value) => value > 0 && value < 1 ? value * 100 : value;
}
