using System;

namespace QuadVitals.Core
{
    /// <summary>
    /// Exponential moving average used to stop the taskbar numbers from twitching
    /// (budget "数值变化不抖动，可选 EMA 平滑").
    ///
    /// The first sample after a reset is taken as-is, so the band shows a real value
    /// immediately instead of ramping up from zero.
    /// </summary>
    public sealed class EmaFilter
    {
        private readonly double _alpha;
        private bool _hasValue;

        /// <param name="alpha">0..1; higher follows the raw signal faster.</param>
        public EmaFilter(double alpha)
        {
            if (double.IsNaN(alpha) || alpha <= 0 || alpha > 1)
                throw new ArgumentOutOfRangeException(nameof(alpha), "alpha must be in (0, 1]");
            _alpha = alpha;
        }

        public double Alpha { get { return _alpha; } }

        public bool HasValue { get { return _hasValue; } }

        public double Value { get; private set; }

        public double Push(double sample)
        {
            if (double.IsNaN(sample) || double.IsInfinity(sample))
            {
                // Never let a bad sample poison the average; keep the last good value.
                return Value;
            }

            if (!_hasValue)
            {
                Value = sample;
                _hasValue = true;
            }
            else
            {
                Value += _alpha * (sample - Value);
            }
            return Value;
        }

        public void Reset()
        {
            _hasValue = false;
            Value = 0;
        }

        /// <summary>
        /// Alpha for "reaches ~90% of a step change within <paramref name="samples"/> samples".
        /// </summary>
        public static double AlphaForSettling(int samples)
        {
            if (samples <= 0) throw new ArgumentOutOfRangeException(nameof(samples));
            return 1.0 - Math.Pow(0.1, 1.0 / samples);
        }
    }
}
