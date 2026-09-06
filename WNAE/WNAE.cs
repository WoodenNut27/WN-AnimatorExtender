using System;
using UnityEngine;

namespace WoodenNut.WNAE
{
    /// <summary>CmpInt / CmpFloat で共通のビット操作。</summary>
    public static class WNAEUtil
    {
        /// <summary>VRChat の同期 Int / Float が消費するビット数。</summary>
        public const int UncompressedCost = 8;

        /// <summary>index の bit 桁目が立っているか。</summary>
        public static bool GetBit(int index, int bit)
        {
            return ((index >> bit) & 1) != 0;
        }
    }

    #region CmpInt

    /// <summary>
    /// 1 個の CmpInt（可変長圧縮 Int）の宣言。
    /// Animator 上では普通の Int パラメータのまま扱われ、ビルド時に実際に使われている値を
    /// 走査して値域を決め、必要ビット数分の同期 Bool へ分解される。
    /// </summary>
    [Serializable]
    public class CmpIntEntry
    {
        /// <summary>Animator / VRCExpressionParameters 上のパラメータ名。</summary>
        public string name = "";

        /// <summary>OFF なら既存の VRCExpressionParameters の Default をそのまま使う。</summary>
        public bool overrideDefaultValue = false;

        /// <summary>overrideDefaultValue が有効なときだけ使われる。</summary>
        public int defaultValue = 0;

        /// <summary>OFF なら既存の VRCExpressionParameters の Saved をそのまま使う。</summary>
        public bool overrideSaved = false;

        /// <summary>
        /// Int の値をアバターに保存するか。overrideSaved が有効なときだけ使われる。
        /// 生成される Bool は毎回 Animator が Set し直すため保存しない。
        /// </summary>
        public bool saved = true;

        /// <summary>
        /// OFF なら値域を自動検出する。OSC など静的に検出できない書き込みがある場合に ON にして手入力する。
        /// </summary>
        public bool rangeOverride = false;

        /// <summary>rangeOverride が有効なときだけ使われる。</summary>
        public int minValue = 0;

        /// <summary>rangeOverride が有効なときだけ使われる。</summary>
        public int maxValue = 7;

        /// <summary>OFF なら生成 Bool 名の接頭辞に "{name}_b" を使う。</summary>
        public bool overrideBoolPrefix = false;

        /// <summary>overrideBoolPrefix が有効なときだけ使われる。</summary>
        public string boolPrefixOverride = "";

        public string BoolName(int index) => CmpIntUtil.BoolName(this, index);
    }

    /// <summary>
    /// 解決済みの Int の値域。ビット数などの派生値はここから求める。
    /// </summary>
    public struct CmpIntRange
    {
        public int Min;
        public int Max;

        public CmpIntRange(int min, int max)
        {
            Min = min;
            Max = max;
        }

        public bool IsValid => Max >= Min;

        /// <summary>取り得る値の個数。</summary>
        public int ValueCount => IsValid ? Max - Min + 1 : 0;

        /// <summary>同期に必要な Bool の本数。</summary>
        public int BitCount => CmpIntUtil.BitCount(ValueCount);

        /// <summary>圧縮によって節約されるビット数。</summary>
        public int SavedBits => WNAEUtil.UncompressedCost - BitCount;

        public int Clamp(int value) => Mathf.Clamp(value, Min, Max);

        /// <summary>値をビット列にエンコードするときの 0 起点のインデックス。</summary>
        public int IndexOf(int value) => Clamp(value) - Min;

        public override string ToString() => $"{Min}..{Max}";
    }

    public static class CmpIntUtil
    {
        /// <summary>valueCount 個の値を表現するのに必要な最小ビット数。</summary>
        public static int BitCount(int valueCount)
        {
            if (valueCount <= 2) return 1;

            // Log2 の丸め誤差を避けるため整数演算で求める
            int bits = 0;
            int capacity = 1;
            while (capacity < valueCount)
            {
                capacity <<= 1;
                bits++;
            }
            return bits;
        }

        public static string BoolPrefix(CmpIntEntry entry)
        {
            return entry.overrideBoolPrefix && !string.IsNullOrEmpty(entry.boolPrefixOverride)
                ? entry.boolPrefixOverride
                : entry.name + "_b";
        }

        public static string BoolName(CmpIntEntry entry, int index)
        {
            return BoolPrefix(entry) + index;
        }
    }

    #endregion

    #region CmpFloat

    /// <summary>
    /// 1 個の CmpFloat（精度圧縮 Float）の宣言。
    /// VRChat の同期 Float は -1〜1 を 8bit で表す固定小数点なので、
    /// 実際に使う範囲と必要な精度（ビット数）を指定して段階数を減らす。
    /// </summary>
    [Serializable]
    public class CmpFloatEntry
    {
        /// <summary>Animator / VRCExpressionParameters 上のパラメータ名。</summary>
        public string name = "";

        /// <summary>OFF なら既存の VRCExpressionParameters の Default をそのまま使う。</summary>
        public bool overrideDefaultValue = false;

        /// <summary>overrideDefaultValue が有効なときだけ使われる。</summary>
        public float defaultValue = 0f;

        /// <summary>OFF なら既存の VRCExpressionParameters の Saved をそのまま使う。</summary>
        public bool overrideSaved = false;

        /// <summary>Float の値をアバターに保存するか。overrideSaved が有効なときだけ使われる。</summary>
        public bool saved = true;

        /// <summary>圧縮する値の下限。常時有効。アニメーション用途では 0 で足りることが多い。</summary>
        public float minValue = 0f;

        /// <summary>圧縮する値の上限。常時有効。</summary>
        public float maxValue = 1f;

        /// <summary>圧縮後のビット数。常時有効。</summary>
        public int bits = CmpFloatUtil.DefaultBits;

        /// <summary>OFF なら生成 Bool 名の接頭辞に "{name}_b" を使う。</summary>
        public bool overrideBoolPrefix = false;

        /// <summary>overrideBoolPrefix が有効なときだけ使われる。</summary>
        public string boolPrefixOverride = "";

        public string BoolName(int index) => CmpFloatUtil.BoolName(this, index);
    }

    /// <summary>
    /// 解決済みの Float の値域と精度。量子化の計算はすべてここに集約する。
    /// </summary>
    public struct CmpFloatRange
    {
        public float Min;
        public float Max;
        public int Bits;

        public CmpFloatRange(float min, float max, int bits)
        {
            Min = min;
            Max = max;
            Bits = bits;
        }

        public bool IsValid =>
            Bits >= CmpFloatUtil.MinBits && Bits <= CmpFloatUtil.MaxBits &&
            Min >= -CmpFloatUtil.RangeLimit && Max <= CmpFloatUtil.RangeLimit &&
            Min < Max;

        /// <summary>表現できる段階数。</summary>
        public int Levels => 1 << Bits;

        /// <summary>段階どうしの間隔。両端を含む正規化なので Levels-1 で割る。</summary>
        public float Step => (Max - Min) / (Levels - 1);

        /// <summary>index 段目が表す値。index=0 で Min、index=Levels-1 で Max。</summary>
        public float ValueOf(int index) => Min + index * Step;

        /// <summary>value を最も近い段階に量子化したときのインデックス。</summary>
        public int IndexOf(float value)
        {
            return Mathf.Clamp(Mathf.RoundToInt((Clamp(value) - Min) / Step), 0, Levels - 1);
        }

        /// <summary>
        /// index 段目に入る値の下側境界。エンコードの遷移条件（Greater）に使う。
        /// </summary>
        public float LowerThreshold(int index) => ValueOf(index) - Step * 0.5f;

        public float Clamp(float value) => Mathf.Clamp(value, Min, Max);

        /// <summary>value を量子化した後の実際の値。</summary>
        public float Quantize(float value) => ValueOf(IndexOf(value));

        /// <summary>圧縮によって節約されるビット数。</summary>
        public int SavedBits => WNAEUtil.UncompressedCost - Bits;

        public override string ToString() => $"{Min}..{Max}";
    }

    public static class CmpFloatUtil
    {
        /// <summary>VRChat の Float パラメータが表せる範囲。</summary>
        public const float RangeLimit = 1f;

        /// <summary>1bit は Bool と変わらないので 2bit から。</summary>
        public const int MinBits = 2;

        /// <summary>8bit は無圧縮と同じなので 7bit まで。</summary>
        public const int MaxBits = 7;

        public const int DefaultBits = 4;

        public static string BoolPrefix(CmpFloatEntry entry)
        {
            return entry.overrideBoolPrefix && !string.IsNullOrEmpty(entry.boolPrefixOverride)
                ? entry.boolPrefixOverride
                : entry.name + "_b";
        }

        public static string BoolName(CmpFloatEntry entry, int index)
        {
            return BoolPrefix(entry) + index;
        }
    }

    #endregion
}
