using System.Globalization;

namespace InkEngine;

/// <summary>
/// 小型函数表达式解析/求值器：只接受数学表达式，不执行代码。
/// 支持 + - * / ^、括号、x、e/pi，以及 sin/cos/tan/asin/acos/atan/sqrt/abs/exp/ln/log/log10。
/// 第一版故意不做隐式乘法（2x → 2*x），让 AI 输出明确写法，减少歧义。
/// </summary>
internal sealed class PlotExpression
{
    private readonly Node _root;

    private PlotExpression(Node root, string text)
    {
        _root = root;
        Text = text;
    }

    public string Text { get; }

    public double Eval(double x)
    {
        double v = _root.Eval(x);
        return double.IsFinite(v) ? v : double.NaN;
    }

    public static PlotExpression Parse(string text,
                                       IReadOnlyDictionary<string, double> parameters = null)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        text = Normalize(text.Trim());
        if (text.StartsWith("y=", StringComparison.OrdinalIgnoreCase))
            text = text[2..].Trim();
        else if (text.StartsWith("y =", StringComparison.OrdinalIgnoreCase))
            text = text[3..].Trim();
        var p = new Parser(text, parameters);
        var root = p.ParseExpression();
        p.ExpectEnd();
        return new PlotExpression(root, text);
    }

    /// <summary>把常见的 Unicode 数学写法换成解析器认识的 ASCII 写法。</summary>
    private static string Normalize(string s)
    {
        return s.Replace("²", "^2", StringComparison.Ordinal)
                .Replace("³", "^3", StringComparison.Ordinal)
                .Replace('×', '*').Replace('÷', '/')
                .Replace('−', '-').Replace('（', '(').Replace('）', ')')
                .Replace("**", "^", StringComparison.Ordinal);
    }

    private abstract class Node
    {
        public abstract double Eval(double x);
    }

    private sealed class NumberNode(double value) : Node
    {
        public override double Eval(double x) => value;
    }

    private sealed class VariableNode : Node
    {
        public override double Eval(double x) => x;
    }

    private sealed class UnaryNode(char op, Node child) : Node
    {
        public override double Eval(double x)
            => op == '-' ? -child.Eval(x) : child.Eval(x);
    }

    private sealed class BinaryNode(char op, Node left, Node right) : Node
    {
        public override double Eval(double x)
        {
            double a = left.Eval(x), b = right.Eval(x);
            return op switch
            {
                '+' => a + b,
                '-' => a - b,
                '*' => a * b,
                '/' => b == 0 ? double.NaN : a / b,
                '^' => PowSmart(a, b),
                _ => double.NaN,
            };
        }

        /// <summary>
        /// 高中数学里的有理次幂：x^(1/3)、x^(2/3)、x^(4/3) 在负数区间也有定义。
        /// Math.Pow 对负数 + 分数指数一律 NaN，会把左半支整段吞掉。
        /// 只补常见分母 3 的情况；1/2、3/2 仍按实数域限制（负数无定义）。
        /// </summary>
        private static double PowSmart(double a, double b)
        {
            if (a >= 0 || double.IsNaN(b) || double.IsInfinity(b))
                return Math.Pow(a, b);
            // 找最近的 n/3（n 为奇数时负数结果，偶数时正数）
            for (int n = -8; n <= 8; n++)
            {
                if (n == 0) continue;
                if (Math.Abs(b - n / 3.0) > 1e-3) continue;
                double mag = Math.Pow(-a, b);
                return (Math.Abs(n) % 2 == 1) ? -mag : mag;
            }
            return Math.Pow(a, b);
        }
    }

    private sealed class FunctionNode(string name, Node arg) : Node
    {
        public override double Eval(double x)
        {
            double v = arg.Eval(x);
            return name switch
            {
                "sin" => Math.Sin(v),
                "cos" => Math.Cos(v),
                "tan" => Math.Tan(v),
                "cot" => Math.Abs(Math.Tan(v)) < 1e-12 ? double.NaN : 1.0 / Math.Tan(v),
                "sec" => Math.Abs(Math.Cos(v)) < 1e-12 ? double.NaN : 1.0 / Math.Cos(v),
                "csc" => Math.Abs(Math.Sin(v)) < 1e-12 ? double.NaN : 1.0 / Math.Sin(v),
                "asin" => Math.Asin(v),
                "acos" => Math.Acos(v),
                "atan" => Math.Atan(v),
                "sqrt" => v < 0 ? double.NaN : Math.Sqrt(v),
                "abs" => Math.Abs(v),
                "exp" => Math.Exp(v),
                "ln" => v <= 0 ? double.NaN : Math.Log(v),
                "log" or "log10" => v <= 0 ? double.NaN : Math.Log10(v),
                "log2" or "log_2" => v <= 0 ? double.NaN : Math.Log2(v),
                "cbrt" => Math.Cbrt(v),
                _ => double.NaN,
            };
        }
    }

    private sealed class Parser(string text, IReadOnlyDictionary<string, double> parameters)
    {
        private int _i;

        private char Cur => _i < text.Length ? text[_i] : '\0';

        private void SkipWs()
        {
            while (_i < text.Length && char.IsWhiteSpace(text[_i])) _i++;
        }

        private bool Take(char c)
        {
            SkipWs();
            if (Cur != c) return false;
            _i++;
            return true;
        }

        public Node ParseExpression()
        {
            var left = ParseTerm();
            while (true)
            {
                if (Take('+')) left = new BinaryNode('+', left, ParseTerm());
                else if (Take('-')) left = new BinaryNode('-', left, ParseTerm());
                else return left;
            }
        }

        private Node ParseTerm()
        {
            var left = ParsePower();
            while (true)
            {
                if (Take('*')) left = new BinaryNode('*', left, ParsePower());
                else if (Take('/')) left = new BinaryNode('/', left, ParsePower());
                else return left;
            }
        }

        private Node ParsePower()
        {
            var left = ParseUnary();
            if (Take('^')) return new BinaryNode('^', left, ParsePower());
            return left;
        }

        private Node ParseUnary()
        {
            if (Take('+')) return ParseUnary();
            if (Take('-')) return new UnaryNode('-', ParseUnary());
            return ParsePrimary();
        }

        private Node ParsePrimary()
        {
            SkipWs();
            if (Take('('))
            {
                var inner = ParseExpression();
                if (!Take(')')) throw new FormatException("缺少右括号");
                return inner;
            }

            if (char.IsDigit(Cur) || Cur == '.')
            {
                int start = _i;
                while (_i < text.Length &&
                       (char.IsDigit(text[_i]) || text[_i] == '.' ||
                        text[_i] == 'e' || text[_i] == 'E' ||
                        ((text[_i] == '+' || text[_i] == '-') && _i > start &&
                         (text[_i - 1] == 'e' || text[_i - 1] == 'E'))))
                    _i++;
                string num = text[start.._i];
                if (!double.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture,
                                     out double v))
                    throw new FormatException("数字格式不对：" + num);
                return new NumberNode(v);
            }

            if (char.IsLetter(Cur) || Cur == '_')
            {
                int start = _i;
                while (_i < text.Length && (char.IsLetterOrDigit(text[_i]) || text[_i] == '_'))
                    _i++;
                string name = text[start.._i].ToLowerInvariant();
                if (name == "x") return new VariableNode();
                if (name == "e") return new NumberNode(Math.E);
                if (name == "pi") return new NumberNode(Math.PI);
                if (parameters != null && parameters.TryGetValue(name, out double pv))
                    return new NumberNode(pv);
                if (IsFunction(name))
                {
                    if (!Take('(')) throw new FormatException($"函数 {name} 缺少括号");
                    var arg = ParseExpression();
                    if (!Take(')')) throw new FormatException($"函数 {name} 缺少右括号");
                    return new FunctionNode(name, arg);
                }
                throw new FormatException(
                    name.Length == 1
                        ? $"未知参数 {name}（含参函数要在 params 里给默认值）"
                        : "未知标识符：" + name);
            }

            throw new FormatException("表达式无法解析");
        }

        public void ExpectEnd()
        {
            SkipWs();
            if (_i != text.Length) throw new FormatException("表达式末尾有多余内容");
        }

        private static bool IsFunction(string n)
            => n is "sin" or "cos" or "tan" or "cot" or "sec" or "csc"
                 or "asin" or "acos" or "atan"
                 or "sqrt" or "abs" or "exp" or "ln" or "log" or "log10"
                 or "log2" or "log_2" or "cbrt";
    }
}
