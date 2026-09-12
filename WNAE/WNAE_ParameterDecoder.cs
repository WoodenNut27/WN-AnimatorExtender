using UnityEngine;

namespace WoodenNut.WNAE
{
    /// <summary>
    /// State に Add Behaviour して、1 個の Int を 8 個の Bool へデコードする。
    /// ビルド時にこの State は Sub State Machine へ置き換えられる。
    ///
    /// StateMachineBehaviour は ScriptableObject 派生なので、Unity の制約により
    /// クラス名とファイル名を一致させないと Add Behaviour の一覧に出てこない。
    /// </summary>
    public class WNAE_ParameterDecoder : StateMachineBehaviour
    {
        /// <summary>デコード対象の Int。</summary>
        public string inputParameter = "";

        /// <summary>
        /// ビット 0（LSB）から順に 8 個の出力先 Bool。
        /// 空文字は「非選択」で、そのビットは出力されない。
        /// </summary>
        public string[] bits = new string[WNAECodec.BitCount];

        /// <summary>
        /// 要素数を補正した配列を返す。
        /// 呼び出し元がビルド中の複製か元アセットかに依存しないよう、
        /// フィールドには書き戻さず、補正が必要なときだけ別の配列を返す。
        /// </summary>
        public string[] NormalizedBits()
        {
            if (bits != null && bits.Length == WNAECodec.BitCount) return bits;

            var normalized = new string[WNAECodec.BitCount];
            for (var i = 0; i < normalized.Length; i++)
            {
                normalized[i] = bits != null && i < bits.Length ? bits[i] : "";
            }

            return normalized;
        }

        /// <summary>出力先が設定されている最下位ビット。1 つも無ければ -1。</summary>
        public int LowestSelectedBit()
        {
            var normalized = NormalizedBits();

            for (var i = 0; i < normalized.Length; i++)
            {
                if (!string.IsNullOrEmpty(normalized[i])) return i;
            }

            return -1;
        }
    }
}
