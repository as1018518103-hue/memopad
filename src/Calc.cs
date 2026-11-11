using System;
using System.Globalization;
using System.Text;

namespace MemoPad
{
    // 随手算：识别光标前的算式，算出结果
    // 支持 + - * / × ÷ ^ % 括号（中英文）、小数、千分位逗号，例如：
    //   12*3.5+8=      → 50
    //   (120+35)×1.15= → 178.25
    //   2400*15%=      → 360
    public static class Calc
    {
        const string Allowed = "0123456789.,+-*/×÷^%()（）  　";

        // text 是光标所在行到 "=" 为止（不含 "="）的内容；返回要补上的结果，失败返回 null
        public static string TryResult(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            int i = text.Length;
            while (i > 0 && Allowed.IndexOf(text[i - 1]) >= 0) i--;
            string expr = text.Substring(i).Trim();
            // 去掉开头多余的符号（但保留负号和左括号）
            while (expr.Length > 0 && "+*/×÷^%),）".IndexOf(expr[0]) >= 0) expr = expr.Substring(1).TrimStart();
            if (expr.Length == 0) return null;

            bool hasDigit = false, hasOp = false;
            for (int k = 0; k < expr.Length; k++)
            {
                char c = expr[k];
                if (char.IsDigit(c)) hasDigit = true;
                else if ("+*/×÷^%".IndexOf(c) >= 0 || (c == '-' && k > 0)) hasOp = true;
            }
            if (!hasDigit || !hasOp) return null;

            double v;
            if (!TryEval(expr, out v)) return null;
            return Format(v);
        }

        public static string Format(double v)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) return null;
            double r = Math.Round(v, 6);
            if (Math.Abs(r) >= 1e15) return r.ToString("G15", CultureInfo.InvariantCulture);
            return r.ToString("0.######", CultureInfo.InvariantCulture);
        }

        public static bool TryEval(string s, out double value)
        {
            value = 0;
            var sb = new StringBuilder();
            foreach (char c in s)
            {
                switch (c)
                {
                    case '×': sb.Append('*'); break;
                    case '÷': sb.Append('/'); break;
                    case '（': sb.Append('('); break;
                    case '）': sb.Append(')'); break;
                    case ' ': case ' ': case '　': break;
                    case ',': break;   // 千分位
                    default: sb.Append(c); break;
                }
            }
            var p = new Parser(sb.ToString());
            try
            {
                value = p.Expr();
                return p.AtEnd;
            }
            catch { return false; }
        }

        class Parser
        {
            readonly string s; int pos;
            public Parser(string s) { this.s = s; }
            public bool AtEnd { get { return pos == s.Length; } }
            char Peek { get { return pos < s.Length ? s[pos] : '\0'; } }

            public double Expr()
            {
                double v = Term();
                while (Peek == '+' || Peek == '-')
                {
                    char op = s[pos++];
                    double r = Term();
                    if (pctTerm) r = v * r;          // 100-20% = 80（和计算器一样）
                    v = op == '+' ? v + r : v - r;
                }
                return v;
            }

            bool pctTerm, pctFactor;
            double Term()
            {
                double v = Power();
                pctTerm = pctFactor;
                while (Peek == '*' || Peek == '/')
                {
                    pctTerm = false;
                    char op = s[pos++];
                    double r = Power();
                    if (op == '/' && r == 0) throw new DivideByZeroException();
                    v = op == '*' ? v * r : v / r;
                }
                return v;
            }

            double Power()
            {
                double b = Unary();
                if (Peek == '^') { pos++; return Math.Pow(b, Power()); }
                return b;
            }

            double Unary()
            {
                if (Peek == '-') { pos++; return -Unary(); }
                if (Peek == '+') { pos++; return Unary(); }
                return Postfix();
            }

            double Postfix()
            {
                double v = Primary();
                pctFactor = false;
                while (Peek == '%') { pos++; v /= 100.0; pctFactor = true; }
                return v;
            }

            double Primary()
            {
                if (Peek == '(')
                {
                    pos++;
                    double v = Expr();
                    if (Peek != ')') throw new FormatException();
                    pos++;
                    return v;
                }
                int start = pos;
                while (char.IsDigit(Peek) || Peek == '.') pos++;
                if (start == pos) throw new FormatException();
                return double.Parse(s.Substring(start, pos - start), CultureInfo.InvariantCulture);
            }
        }
    }
}
