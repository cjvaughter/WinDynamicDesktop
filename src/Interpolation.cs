using System;

namespace WinDynamicDesktop
{
    public enum InterpolationMethod
    {
        None,
        Linear,
        Quad,
        Cubic,
        Quart,
        Quint,
        Sine,
        Circle,
        Exponential
    }

    public static class Interpolation
    {
        public static double Calculate(double value, InterpolationMethod method)
        {
            if (value < 0)
            {
                return 0;
            }
            else if (value > 1)
            {
                return 1;
            }

            switch (method)
            {
                default:
                case InterpolationMethod.None:
                    return 0;

                case InterpolationMethod.Linear:
                    return value;

                case InterpolationMethod.Quad:
                    return InOut(value, Quad);

                case InterpolationMethod.Cubic:
                    return InOut(value, Cubic);

                case InterpolationMethod.Quart:
                    return InOut(value, Quart);

                case InterpolationMethod.Quint:
                    return InOut(value, Quint);

                case InterpolationMethod.Sine:
                    return InOut(value, Sine);

                case InterpolationMethod.Circle:
                    return InOut(value, Circle);

                case InterpolationMethod.Exponential:
                    return InOut(value, Exponential);
            }
        }

        private static double InOut(double value, Func<double, double> func)
        {
            if (value >= 0.5)
            {
                return (1 - func((1 - value) * 2)) / 2 + 0.5;

            }
            return func(value * 2) / 2;
        }

        private static double Quad(double value) => Math.Pow(value, 2);
        private static double Cubic(double value) => Math.Pow(value, 3);
        private static double Quart(double value) => Math.Pow(value, 4);
        private static double Quint(double value) => Math.Pow(value, 5);
        private static double Exponential(double value) => (Math.Exp(2 * value) - 1) / (Math.Exp(2) - 1);
        private static double Sine(double value) => 1 - Math.Sin(Math.PI / 2 * (1 - value));
        private static double Circle(double value) => 1 - Math.Sqrt(1.0 - value * value);
    }
}
