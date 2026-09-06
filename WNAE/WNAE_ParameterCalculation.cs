using UnityEngine;

namespace WoodenNut.WNAE
{
    public enum WNAECalcOperation
    {
        Add,
        Sub,
        Mul,
        Div,
        Mod,
        Not,
        And,
        Or,
        Xor,
        Xnor,
        ShiftLeft,
        ShiftRight,

        // 既存の Behaviour に保存された値がずれないよう、追加は必ず末尾に行うこと
        Copy,
    }

    /// <summary>
    /// State に Add Behaviour して <c>c = a op b</c> を宣言する。
    /// ビルド時にこの State の後ろへ Sub State Machine が生成され、
    /// Parameter Driver と遷移条件の組み合わせに展開される。
    ///
    /// StateMachineBehaviour は ScriptableObject 派生なので、Unity の制約により
    /// クラス名とファイル名を一致させないと Add Behaviour の一覧に出てこない。
    /// </summary>
    public class WNAE_ParameterCalculation : StateMachineBehaviour
    {
        public WNAECalcOperation operation = WNAECalcOperation.Add;

        /// <summary>入力 1。</summary>
        public string parameterA = "";

        /// <summary>入力 2。Not では使われない。</summary>
        public string parameterB = "";

        /// <summary>出力。a / b と同じパラメータを指定してもよい。</summary>
        public string parameterC = "";

        /// <summary>
        /// 演算のビット幅。生成される State 数を大きく左右するので、
        /// 実際に使う値域に合わせて絞るのが望ましい。
        /// </summary>
        [Range(WNAECalc.MinBitWidth, WNAECalc.MaxBitWidth)]
        public int bitWidth = WNAECalc.DefaultBitWidth;
    }

    public static class WNAECalc
    {
        public const int MinBitWidth = 1;
        public const int MaxBitWidth = 8;
        public const int DefaultBitWidth = 8;

        /// <summary>この演算が b を使うか。</summary>
        public static bool UsesB(WNAECalcOperation operation)
        {
            return operation != WNAECalcOperation.Not && operation != WNAECalcOperation.Copy;
        }

        /// <summary>オーバーフロー時に折り返す法。2 の冪なので上位ビットを落とすだけで求まる。</summary>
        public static int Modulus(int bitWidth)
        {
            return 1 << Mathf.Clamp(bitWidth, MinBitWidth, MaxBitWidth);
        }

        public static string DisplayName(WNAECalcOperation operation)
        {
            switch (operation)
            {
                case WNAECalcOperation.Add: return "Add (c = a + b)";
                case WNAECalcOperation.Sub: return "Sub (c = a - b)";
                case WNAECalcOperation.Mul: return "Mul (c = a * b)";
                // Unity の Popup は "/" を階層区切りとして解釈してしまうため除算記号を使う
                case WNAECalcOperation.Div: return "Div (c = a ÷ b)";
                case WNAECalcOperation.Mod: return "Mod (c = a % b)";
                case WNAECalcOperation.Not: return "NOT (c = ~a)";
                case WNAECalcOperation.And: return "AND (c = a & b)";
                case WNAECalcOperation.Or: return "OR (c = a | b)";
                case WNAECalcOperation.Xor: return "XOR (c = a ^ b)";
                case WNAECalcOperation.Xnor: return "XNOR (c = ~(a ^ b))";
                case WNAECalcOperation.ShiftLeft: return "L-SHIFT (c = a << b)";
                case WNAECalcOperation.ShiftRight: return "R-SHIFT (c = a >> b)";
                case WNAECalcOperation.Copy: return "Copy (c = a)";
                default: return operation.ToString();
            }
        }
    }
}
