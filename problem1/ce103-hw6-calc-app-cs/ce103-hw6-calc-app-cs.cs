using Loyc;
using Loyc.Collections;
using Loyc.Syntax;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace LesGraphingCalc
{
    // Holds output data and output bitmap, and performs rendering
    class OutputState
    {
        public List<CalculatorCore> Calcs;
        public GraphRange XRange, YRange, ZRange;
        public DirectBitmap Bitmap;
        
        public OutputState(List<CalculatorCore> calcs, GraphRange xRange, GraphRange yRange, GraphRange zRange, DirectBitmap bitmap)
        {
            Calcs = calcs;
            XRange = xRange;
            YRange = yRange;
            ZRange = zRange;
            Bitmap = bitmap;
        }

        internal void RenderAll()
        {
            using (var g = Graphics.FromImage(Bitmap.Bitmap)) {
                g.Clear(Color.White);

                // Y-range can be autodetected only if there are no "3D" functions already using Y
                if (YRange.AutoRange && Calcs.All(c => c.Results is double[])) {
                    YRange.Lo = -0.5;
                    YRange.Hi = 1;
                    Calcs.ForEach(c => FindMinMax((double[])c.Results, ref YRange.Lo, ref YRange.Hi));
                }

                // Draw at most one "heat map"
                var heatMap = Calcs.FirstOrDefault(c => (c as Calculator3D)?.EquationMode == false);
                if (heatMap != null) {
                    MaybeChooseAutoZRange(ZRange, (double[,])heatMap.Results);
                    RenderXYFunc((double[,])heatMap.Results, false, Color.Transparent);
                }

                int seriesIndex = 0;
                foreach (var calc in Calcs)
                    RenderSeries(calc, seriesIndex++, g);

                DrawGridLines(g);
            }
        }

        void RenderSeries(CalculatorCore calc, int seriesIndex, Graphics g)
        {
            sbyte[,] bins = null;
            using (Pen pen = MakePen(calc.Expr, seriesIndex)) {
                if (calc.Results is double[])
                    RenderXFunc(g, (double[])calc.Results, pen);
                else {
                    double[,] data = (double[,])calc.Results;
                    if (((Calculator3D)calc).EquationMode)
                        RenderXYFunc(data, true, pen.Color);
                    else {
                        bins = bins ?? new sbyte[data.GetLength(0), data.GetLength(1)];
                        double lo, interval = ChooseGridSpacing(out lo, ZRange, data);
                        RenderContourLines(data, bins, pen.Color, lo, interval);
                        DrawText(g, "Contour interval: {0}".Localized(interval), Bitmap.Width / 2, 5, pen.Color, StringAlignment.Center, StringAlignment.Near);
                    }
                }
            }
        }

        public Font Font = new Font(FontFamily.GenericSansSerif, 10);

        private SizeF DrawText(Graphics g, string text, float x, float y, Color color, StringAlignment hAlign, StringAlignment vAlign)
        {
            using (Brush brush = new SolidBrush(color)) {
                SizeF size = g.MeasureString(text, Font);
                x = Align(x, size.Width, hAlign);
                y = Align(y, size.Height, vAlign);
                using (var rectbrush = new SolidBrush(Color.FromArgb(128, Color.White)))
                    g.FillRectangle(rectbrush, x, y, size.Width, size.Height);
                g.DrawString(text, Font, Brushes.White, x-1, y);
                g.DrawString(text, Font, Brushes.White, x+1, y);
                g.DrawString(text, Font, Brushes.White, x, y-1);
                g.DrawString(text, Font, Brushes.White, x, y+1);
                g.DrawString(text, Font, brush, x, y);
                return size;
            }
        }
        static float Align(float x, float size, StringAlignment align) => 
            align == StringAlignment.Center ? x - size/2 : 
            align == StringAlignment.Far    ? x - size : x;

        static readonly Color[] SeriesColors = new Color[] {
            Color.DarkGreen, Color.Teal, Color.MediumBlue, Color.MediumPurple, Color.DeepPink, Color.Orange, Color.Brown, Color.Black, 
            Color.LawnGreen, Color.DarkTurquoise, Color.DodgerBlue, Color.Fuchsia, Color.Red, Color.Salmon, Color.PeachPuff
        };

        internal static Pen MakePen(LNode expr, int seriesIndex = -1) // -1 = axis line
        {
            float lineWidth = (seriesIndex + 1) % 3 + 1;
            DashStyle dash = DashStyle.Solid, dash_;
            Color color = Color.MidnightBlue;
            if (seriesIndex > -1)
                color = SeriesColors[seriesIndex % SeriesColors.Length];

            foreach (var attr in expr.Attrs.Where(a => a.IsId)) {
                // Try to interpret identifier as a color or dash style
                if (!TryInterpretAsColor(attr, ref color)) {
                    if (Enum.TryParse<DashStyle>(attr.Name.Name, true, out dash_))
                        dash = dash_;
                }
            }
            if (seriesIndex > -1)
                foreach (var attr in expr.Attrs.Where(a => a.IsLiteral)) {
                    // Interpret literal as a line width
                    lineWidth = Convert.ToInt32(attr.Value, null);
                }
            return new Pen(color, lineWidth) { DashStyle = dash, EndCap = LineCap.DiamondAnchor };
        }
        internal static bool TryInterpretAsColor(LNode attr, ref Color color)
        {
            if (attr.IsId) {
                var p = typeof(Color).GetProperty(attr.Name.Name, BindingFlags.IgnoreCase | BindingFlags.Public | BindingFlags.Static);
                if (p != null) {
                    color = (Color)p.GetMethod.Invoke(null, null);
                    return true;
                }
            }
            return false;
        }

        void RenderXFunc(Graphics g, double[] data, Pen pen)
        {
            if (data.Length == 0 || YRange.Lo >= YRange.Hi)
                return;

            g.SmoothingMode = SmoothingMode.AntiAlias;
            PointF[] points = new PointF[data.Length];
            int x;
            for (x = 0; x < data.Length; x++) {
                float y = (float)(YRange.PxCount - 1 - YRange.ValueToPx(data[x]));
                points[x] = new PointF(x, (float)y);
            }
            DrawLinesSafe(g, pen, points);
        }

        // Same as g.DrawLines(), except it ignores NaN/inf instead of throwing
        private void DrawLinesSafe(Graphics g, Pen pen, PointF[] points)
        {
            int x, start = 0;
            for (x = 0; x < points.Length; x++) {
                if (float.IsNaN(points[x].Y) || float.IsInfinity(points[x].Y)) {
                    if (x > start)
                        DrawLinesWorkaround(g, pen, points.Slice(start, x - start).ToArray());
                    start = x + 1;
                }
            }
            if (x > start)
                DrawLinesWorkaround(g, pen, points.Slice(start).ToArray());
        }
        // Same as g.DrawLines(), except it works and doesn't throw when given a 
        // single point or large values of Y
        private void DrawLinesWorkaround(Graphics g, Pen pen, PointF[] points)
        {
            for (int i = 0; i < points.Length; i++) {
                if (points[i].Y > 10000000) points[i].Y = 10000000;
                if (points[i].Y < -10000000) points[i].Y = -10000000;
            }
            if (points.Length == 1)
                g.DrawLines(pen, new[] { points[0], new PointF(points[0].X + 0.1f, points[0].Y) });
            else if (points.Length > 1)
                g.DrawLines(pen, points);
        }

        static readonly Color[] ColorBands = new Color[] { Color.Orange, Color.Red, Color.Fuchsia, Color.RoyalBlue, Color.White, Color.Goldenrod, Color.Lime, Color.Blue, Color.Black };
        static Color[] HeatColors = null;

        void RenderXYFunc(double[,] data, bool booleanMode, Color trueColor)
        {
            if (HeatColors == null) {
                HeatColors = new Color[(ColorBands.Length - 1) * 16];
                for (int band = 0; band < ColorBands.Length - 1; band++) {
                    Color lo = ColorBands[band], hi = ColorBands[band + 1];
                    for (int hii = 0; hii < 16; hii++) {
                        int loi = 16 - hii;
                        HeatColors[band * 16 + hii] = Color.FromArgb((lo.R * loi + hi.R * hii) >> 4, 
                                                                     (lo.G * loi + hi.G * hii) >> 4, 
                                                                     (lo.B * loi + hi.B * hii) >> 4);
                    }
                }
            }
            Color nanColor = Color.FromArgb(trueColor.R / 2 + 64, trueColor.G / 2 + 64, trueColor.B / 2 + 64);
            for (int y = 0; y < data.GetLength(0); y++) {
                for (int x = 0; x < data.GetLength(1); x++) {
                    double d = data[y, x];
                    if (!booleanMode) {
                        ZRange.PxCount = HeatColors.Length;
                        double z = ZRange.ValueToPx(data[y, x]);
                        Color c;
                        if (double.IsNaN(z))
                            c = Color.DarkGray; // which is lighter than "Gray"
                        else if (double.IsInfinity(z))
                            c = Color.Purple;
                        else {
                            int z2 = (int)G.PutInRange(ZRange.ValueToPx(data[y, x]), 0, HeatColors.Length - 1);
                            c = HeatColors[z2];
                        }                        
                        Bitmap.SetPixel(x, Bitmap.Height - 1 - y, c);
                    } else if (!(d == 0)) {
                        Bitmap.SetPixel(x, Bitmap.Height - 1 - y, double.IsNaN(d) ? nanColor : trueColor);
                    }
                }
            }
        }

        void RenderContourLines(double[,] data, sbyte[,] bins, Color color, double lo, double interval)
        {
            if (color.A == 0)
                return; // Transparent: user turned off contour lines
            double frequency = 1.0 / interval;
            int w = data.GetLength(1), h = data.GetLength(0);
            for (int y = 0; y < data.GetLength(0); y++) {
                for (int x = 0; x < data.GetLength(1); x++) {
                    double d = data[y, x];
                    bins[y, x] = (sbyte)(d < ZRange.Lo ? -2 : d >= ZRange.Hi ? 127 : (int)Math.Floor((d - lo) * frequency));
                }
            }
            for (int y = 0; y < data.GetLength(0) - 1; y++) {
                for (int x = 0; x < data.GetLength(1) - 1; x++) {
                    var b = bins[y, x];
                    if (b != bins[y, x + 1] || b != bins[y + 1, x] || b != bins[y + 1, x + 1])
                        Bitmap.SetPixel(x, YRange.PxCount - 1 - y, color);
                }
            }
        }

        static double Min(double a, double b) => double.IsNaN(a) ? b : (a > b ? b : a);
        static double Max(double a, double b) => double.IsNaN(a) ? b : (a < b ? b : a);

        static void FindMinMax(double[] data, ref double min, ref double max)
        {
            foreach (double d in data)
                if (!double.IsInfinity(d)) {
                    min = Min(min, d);
                    max = Max(max, d);
                }
        }
        static void FindMinMax(double[,] data, ref double min, ref double max)
        {   // Seems like there ought to be a way to do this in fewer LoC.
            // Better to have used jagged arrays?
            for (int y = 0; y < data.GetLength(0); y++) {
                for (int x = 0; x < data.GetLength(1); x++) {
                    var d = data[y, x];
                    if (!double.IsInfinity(d)) {
                        min = Min(min, d);
                        max = Max(max, d);
                    }
                }
            }
        }

        #region Interval detection and grid line drawing

        static double ChooseGridSpacing(out double lo, GraphRange range, double[,] data = null)
        {
            lo = range.Lo;
            MaybeChooseAutoZRange(range, data);
            return ChooseGridSpacing(ref lo, range.Hi, range.RoughLineCount);
        }

        static double ChooseGridSpacing(ref double lo, double hi, int roughLines)
        {
            double dif = hi - lo;
            if (dif <= 0)
                return 1; // avoid unexpected results from negative range

            // Start with an interval that is too large, and reduce it until we have enough lines.
            double interval = Math.Pow(10, Math.Ceiling(Math.Log10(dif)));
            int third = 0;
            for (double roughLinesNow = 1; roughLinesNow < roughLines; third = (third + 1) % 3) {
                // interval is multiplied cumulatively by 0.1 every three steps
                double ratio = third == 2 ? 0.4 : 0.5;
                interval *= ratio;
                roughLinesNow /= ratio;
                if (interval > dif) roughLinesNow = 1;
            }
            lo = Math.Ceiling(lo / interval) * interval;
            return interval;
        }

        private static void MaybeChooseAutoZRange(GraphRange range, double[,] data)
        {
            if (data != null && range.AutoRange) {
                range.Lo = double.NaN;
                range.Hi = double.NaN;
                FindMinMax(data, ref range.Lo, ref range.Hi);
                if (double.IsNaN(range.Lo)) {
                    range.Lo = -1;
                    range.Hi = 1;
                }
            }
        }

        void DrawGridLines(Graphics g)
        {
            // Draw vertical lines based on X axis
            double xLo, xInterval = ChooseGridSpacing(out xLo, XRange);
            for (double x = xLo; x <= XRange.Hi; x += xInterval) {
                float px = (float)XRange.ValueToPx(x);
                g.DrawLine(XRange.LinePen, px, 0, px, YRange.PxCount);
            }
            if (XRange.Lo <= 0 && 0 <= XRange.Hi) {
                float px = (float)XRange.ValueToPx(0);
                g.DrawLine(XRange.AxisPen, px, 0, px, YRange.PxCount);
            }

            // Draw horizontal lines based on Y axis
            double yLo, yInterval = ChooseGridSpacing(out yLo, YRange);
            for (double y = yLo; y <= YRange.Hi; y += yInterval) {
                float px = YRange.PxCount - 1 - (float)YRange.ValueToPx(y);
                g.DrawLine(YRange.LinePen, 0, px, XRange.PxCount, px);
            }
            if (YRange.Lo <= 0 && 0 <= YRange.Hi) {
                float px = YRange.PxCount - 1 - (float)YRange.ValueToPx(0);
                g.DrawLine(YRange.AxisPen, 0, px, XRange.PxCount, px);
            }

            // Draw numeric labels
            float lastWidth = 0, lastPx = -1000;
            for (double x = xLo; x <= XRange.Hi; x += xInterval) {
                float px = (float)XRange.ValueToPx(x);
                if (px > 20 && px - lastPx > lastWidth) {
                    lastWidth = DrawText(g, x.ToString("G7"), px, Bitmap.Height - 4, XRange.AxisPen.Color, StringAlignment.Center, StringAlignment.Far).Width;
                    lastPx = px;
                }
            }
            for (double y = yLo; y <= YRange.Hi; y += yInterval) {
                float px = Bitmap.Height - 1 - (float)YRange.ValueToPx(y);
                if (px > 10 && px < Bitmap.Height-40) {
                    DrawText(g, y.ToString("G7"), 4, px, YRange.AxisPen.Color, StringAlignment.Near, StringAlignment.Center);
                }
            }
        }

        #endregion

        internal string GetMouseOverText(Point pt)
        {
            int x = pt.X, y = YRange.PxCount - 1 - pt.Y;
            double xval = XRange.PxToValue(x);
            double yval = YRange.PxToValue(y);
            if (Calcs.Count == 1) {
                var val = Calcs[0].GetValueAt(x, y);
                if (val != null) {
                    if (Calcs[0] is Calculator3D)
                        return "{0} @ ({1:G4}, {2:G4})".Localized(val.Value, xval, yval);
                    else
                        return "{0} @ X = {1:G8}".Localized(val.Value, xval);
                }
            } else if (Calcs.Count >= 2) {
                var fmt = Calcs.Count == 2 ? "G8" : "G5";
                return "{1} @ X = {0:G4}".Localized(xval,
                    string.Join("; ", Calcs.Select(c => (c.GetValueAt(x, y) ?? double.NaN).ToString(fmt))));
            }
            return "";
        }
    }

    class GraphRange : CalcRange
    {
        public Pen AxisPen;
        public Pen LinePen;
        public string Label;
        public bool AutoRange;
        public int RoughLineCount = 20;
        public LNode RangeExpr;

        public GraphRange(double lo, double hi, int pxCount, Pen pen, string label) : base(lo, hi, pxCount)
        {
            AxisPen = pen;
            Label = label ?? "";
            LinePen = new Pen(Color.FromArgb(128, AxisPen.Color), 1f) { DashStyle = pen.DashStyle };
            AutoRange = lo >= hi;
        }

        public static GraphRange New(string rangeName, LNode range, int numPixels, Dictionary<Symbol,LNode> varDict)
        {
            if (range == null)
                return new GraphRange(-1, 1, numPixels, Pens.MidnightBlue, null) { AutoRange = true };
            if (range.Calls(CodeSymbols.Colon, 2) && range[0].IsId && string.Compare(range[0].Name.Name, rangeName, true) == 0)
                range = range[1]; // ignore axis prefix like "x:" or "y:"
            
            if (range.Calls(CodeSymbols.Sub, 2) || range.Calls(CodeSymbols.DotDot, 2))
            {
                double lo = CalculatorCore.Eval(range[0], varDict);
                double hi = CalculatorCore.Eval(range[1], varDict);
                Pen pen = OutputState.MakePen(range);
                string label = range.Attrs.Select(a => a.Value as string).FirstOrDefault(s => s != null);

                var result = new GraphRange(lo, hi, numPixels, pen, label) { RangeExpr = range };

                foreach (var attr in range.Attrs)
                    if (attr.Value is int)
                        result.RoughLineCount = (int)attr.Value;
                return result;
            }
            throw new FormatException("Invalid range for {axis}: {range}".Localized("axis", rangeName, "range", range));
        }


    }
}
namespace calculator
{
    class RPN
    {
        static public double Calculate(string input)
        {
            try { return double.Parse(GetExpression(input)); }
            catch (Exception) { return Counting(GetExpression(input)); }

        }

        static private string GetExpression(string input)
        {
            string output = string.Empty;
            string fun = string.Empty;
            Stack<char> operStack = new Stack<char>();
            char k = ' '; string p = "";
            for (int i = 0; i < input.Length; i++)
            {
                if (IsOperator(input[i]) || Char.IsDigit(input[i]))
                {
                    if (k == ' ')
                        k = input[i];
                    else
                        if (input[i] == '-' && !Char.IsDigit(k))
                        p += " 0 ";
                    k = input[i];
                }
                p += input[i];
            }
            input = p;
            for (int i = 0; i < input.Length; i++)
            {
                if (IsDelimeter(input[i]))
                    continue;
                if (Char.IsDigit(input[i]))
                {
                    while (!IsDelimeter(input[i]) && !IsOperator(input[i]))
                    {
                        output += input[i];
                        i++;
                        if (i == input.Length) break;
                    }
                    output += " ";
                    i--;
                }
                else
                    if (IsOperator(input[i]))
                {
                    if (input[i] == '(')
                        operStack.Push(input[i]);
                    else if (input[i] == ')')
                    {
                        char s = operStack.Pop();
                        while (s != '(')
                        {
                            output += s.ToString() + ' ';
                            s = operStack.Pop();
                        }
                    }
                    else
                    {
                        if (operStack.Count > 0)
                            if (GetPriority(input[i]) <= GetPriority(operStack.Peek()))
                                output += operStack.Pop().ToString() + " ";

                        operStack.Push(char.Parse(input[i].ToString()));

                    }
                }
                else if (input[i] == '\u03C0')
                    output += " \u03C0 ";
                else if (input[i] == 'e')
                    output += " e ";
                else
                {
                    fun = String.Empty;
                    while (input[i] != '(')
                    {
                        fun += input[i];
                        i++;
                        if (i == input.Length) break;
                    }
                    i++;
                    if (IsFunction(fun))
                    {
                        String param = String.Empty;
                        while (input[i] != ')')
                        {
                            param += input[i];
                            i++;
                            if (i == input.Length) break;
                        }
                        double d;
                        try { d = double.Parse(param); }
                        catch (Exception) { d = Counting(GetExpression(param)); }
                        output += doFunc(fun, d);
                    }
                }
            }
            while (operStack.Count > 0)
                output += operStack.Pop() + " ";

            return output;
        }

        static private double Counting(string input)
        {
            double result = 0;
            double b = 0;
            Stack<double> temp = new Stack<double>();
            try { return double.Parse(input); }
            catch (Exception)
            {
                for (int i = 0; i < input.Length; i++)
                {
                    if (Char.IsDigit(input[i]))
                    {
                        string a = string.Empty;

                        while (!IsDelimeter(input[i]) && !IsOperator(input[i]))
                        {
                            a += input[i];
                            i++;
                            if (i == input.Length) break;
                        }
                        temp.Push(double.Parse(a));
                        i--;
                    }
                    else if (input[i] == '\u03C0')
                        temp.Push(Math.PI);
                    else if (input[i] == 'e')
                        temp.Push(Math.E);
                    else if (IsOperator(input[i]))
                    {
                        double a = temp.Pop();
                        try
                        { b = temp.Pop(); }
                        catch (Exception) { b = 0; }

                        switch (input[i])
                        {
                            case '!': result = factorial((int)a); break;
                            case 'P': result = factorial((int)b) / factorial((int)(b - a)); break;
                            case 'C': result = factorial((int)b) / (factorial((int)a) * factorial((int)(b - a))); break;
                            case '^': result = Math.Pow(b, a); break;
                            case '%': result = b % a; break;
                            case '+': result = b + a; break;
                            case '-': result = b - a; break;
                            case '*': result = b * a; break;
                            case '/': if (a == 0) throw new DividedByZeroException(); else result = b / a; break;
                        }
                        temp.Push(result);
                    }
                }
                try { return temp.Peek(); }
                catch (Exception) { throw new SyntaxException(); }

            }

        }
        static private bool IsDelimeter(char c)
        {
            if ((" =".IndexOf(c) != -1))
                return true;
            return false;
        }
        static private bool IsOperator(char с)
        {
            if (("+-/*^()PC!%".IndexOf(с) != -1))
                return true;
            return false;
        }
        static private bool IsFunction(String s)
        {
            String[] func = { "sin", "cos", "tg", "asin", "acos", "atg", "sqrt", "ln", "lg" };
            if (Array.Exists(func, e => e == s))
                return true;
            return false;
        }
        static private String doFunc(String fun, double param)
        {
            switch (fun)
            {
                case "cos": return Math.Cos(param).ToString();
                case "sin": return Math.Sin(param).ToString();
                case "tg": if (Math.Abs(param % (2 * Math.PI)) == (Math.PI / 2)) throw new TgException(param); else return Math.Tan(param).ToString();
                case "asin": if (param < -1 || param > 1) throw new ArcSinCosException(param); else return Math.Asin(param).ToString();
                case "acos": if (param < -1 || param > 1) throw new ArcSinCosException(param); else return Math.Acos(param).ToString();
                case "atg": return Math.Atan(param).ToString();
                case "sqrt": if (param < 0) throw new SqrtException(param); else return Math.Sqrt(param).ToString();
                case "ln": if (param <= 0) throw new LogException(param); else return Math.Log(param).ToString();
                case "lg": if (param <= 0) throw new LogException(param); else return Math.Log10(param).ToString();
                default: return "";
            }
        }
        static private byte GetPriority(char s)
        {
            switch (s)
            {
                case '(': return 0;
                case ')': return 1;
                case '+': return 2;
                case '-': return 3;
                case '!': return 4;
                case '%': return 4;
                case '*': return 4;
                case '/': return 4;
                case '^': return 5;
                default: return 4;
            }
        }
        private static int factorial(int x)
        {
            int i = 1;
            for (int s = 1; s <= x; s++)
                i = i * s;
            if (x < 0) throw new NegativeFactorialException(x);
            return i;
        }
    }
    public class MyException : Exception
    {
        public string type;
    }
    public class NegativeFactorialException : MyException
    {
        public NegativeFactorialException(int x)
        {
            this.type = "Math error";
            MessageBox.Show("Factorial(" + x + ") does not exsists", type, MessageBoxButtons.OK);
        }
    }
    public class TgException : MyException
    {
        public TgException(double x)
        {
            this.type = "Math error";
            MessageBox.Show("Tg(" + x + ") does not exsists", type, MessageBoxButtons.OK);
        }
    }
    public class SqrtException : MyException
    {
        public SqrtException(double x)
        {
            this.type = "Math error";
            MessageBox.Show("Sqrt(" + x + ") does not exsists", type, MessageBoxButtons.OK);
        }
    }
    public class DividedByZeroException : MyException
    {
        public DividedByZeroException()
        {
            this.type = "Math error";
            MessageBox.Show("Division by zero is impossible", type, MessageBoxButtons.OK);
        }
    }
    public class LogException : MyException
    {
        private Severity severity;
        private object ctx;
        private string msg;
        private double param;

        public LogException(double param)
        {
            this.param = param;
        }

        public LogException(Severity severity, object ctx, double x)
        {
            this.type = "Math error";
            MessageBox.Show("Log(" + x + ") does not exsists", type, MessageBoxButtons.OK);
        }

        public LogException(Severity severity, object ctx, string msg)
        {
            this.severity = severity;
            this.ctx = ctx;
            this.msg = msg;
        }
    }
    public class SyntaxException : MyException
    {
        public SyntaxException()
        {
            this.type = "Syntax error";
            MessageBox.Show("You made a mistake", type, MessageBoxButtons.OK);
        }
    }
    public class ArcSinCosException : MyException
    {
        public ArcSinCosException(double x)
        {
            this.type = "Math error";
            MessageBox.Show("Acos(or Asin) (" + x + ") does not exsists", type, MessageBoxButtons.OK);
        }
    }
}


