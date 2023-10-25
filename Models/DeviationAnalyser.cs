using Microsoft.ML;
using System.Collections.Generic;
using System;
using System.Linq;
namespace NetworkMonitor.ML.Model;

public class DeviationAnalyzer
{
    private readonly int _windowSize;
    private readonly double _threshold;
    private Queue<double> _deviations;

    public DeviationAnalyzer(int windowSize, double threshold)
    {
        _windowSize = windowSize;
        _threshold = threshold;
        _deviations = new Queue<double>(_windowSize);
    }

    public bool IsDeviationSustained(double predicted, double actual)
    {
        double deviation = Math.Abs(predicted - actual);
        if (_deviations.Count >= _windowSize)
        {
            _deviations.Dequeue();
        }
        _deviations.Enqueue(deviation);

        double averageDeviation = _deviations.Average();

        return averageDeviation > _threshold;
    }
}
