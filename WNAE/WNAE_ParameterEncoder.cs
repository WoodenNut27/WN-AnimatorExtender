using System;
using UnityEngine;

namespace WoodenNut.WNAE
{
    /// <summary>Encoder の 1 ビットに何を入力するか。</summary>
    public enum WNAEBitSource
    {
        /// <summary>常に 0。</summary>
        ConstantZero,

        /// <summary>常に 1。</summary>
        ConstantOne,

        /// <summary>Bool パラメータの値。</summary>
        Parameter,
    }

    [Serializable]
    public class WNAEEncoderBit
    {
        public WNAEBitSource source = WNAEBitSource.ConstantZero;

        /// <summary>source が Parameter のときだけ使われる。</summary>
        public string parameterName = "";
    }

    /// <summary>
    /// State に Add Behaviour して、8 個の Bool を 1 個の Int へエンコードする。
    /// ビルド時にこの State は Sub State Machine へ置き換えられる。
    ///
    /// StateMachineBehaviour は ScriptableObject 派生なので、Unity の制約により
    /// クラス名とファイル名を一致させないと Add Behaviour の一覧に出てこない。
    /// </summary>
    public class WNAE_ParameterEncoder : StateMachineBehaviour
    {
        /// <summary>エンコード結果の出力先。</summary>
        public string outputParameter = "";

        /// <summary>ビット 0（LSB）から順に 8 個。</summary>
        public WNAEEncoderBit[] bits = CreateBits();

        private static WNAEEncoderBit[] CreateBits()
        {
            var created = new WNAEEncoderBit[WNAECodec.BitCount];
            for (var i = 0; i < created.Length; i++) created[i] = new WNAEEncoderBit();
            return created;
        }

        /// <summary>
        /// 要素数と null を補正した配列を返す。
        /// 呼び出し元がビルド中の複製か元アセットかに依存しないよう、
        /// フィールドには書き戻さず、補正が必要なときだけ別の配列を返す。
        /// </summary>
        public WNAEEncoderBit[] NormalizedBits()
        {
            var wellFormed = bits != null && bits.Length == WNAECodec.BitCount;

            if (wellFormed)
            {
                foreach (var bit in bits)
                {
                    if (bit == null)
                    {
                        wellFormed = false;
                        break;
                    }
                }
            }

            if (wellFormed) return bits;

            var normalized = new WNAEEncoderBit[WNAECodec.BitCount];
            for (var i = 0; i < normalized.Length; i++)
            {
                normalized[i] = bits != null && i < bits.Length && bits[i] != null
                    ? bits[i]
                    : new WNAEEncoderBit();
            }

            return normalized;
        }
    }
}
