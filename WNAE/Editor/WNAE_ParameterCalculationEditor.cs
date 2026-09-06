using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf.animator;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

namespace WoodenNut.WNAE
{
    #region Builder

    /// <summary>
    /// <see cref="WNAE_ParameterCalculation"/> を、Parameter Driver と遷移条件だけで構成された
    /// チェーンへ展開する。State の差し替えや遷移の張り替えは
    /// <see cref="WNAEBehaviourExpander"/> が共通で行う。
    ///
    /// Parameter Driver の Add は定数加算しかできないため、演算の基本形は
    /// 「入力の最上位の立っているビットを直接消しながら acc に定数を足し込む」
    /// ディスパッチ（<see cref="WNAEChain.GreedyDispatch"/>）になる。
    /// </summary>
    internal class WNAECalcBuilder : IWNAEBehaviourBuilder
    {
        public static readonly WNAECalcBuilder Instance = new WNAECalcBuilder();

        public string DisplayName => "Parameter Calculation";
        public string ParameterPrefix => "WNAE/Calc/";
        public string SubStateMachinePrefix => "WNAE Calc/";

        public bool Matches(StateMachineBehaviour behaviour) => behaviour is WNAE_ParameterCalculation;

        public List<WNAEIssue> Validate(StateMachineBehaviour behaviour)
        {
            return Validate((WNAE_ParameterCalculation)behaviour);
        }

        public void Prepare(
            VirtualAnimatorController controller, WNAEChainContext ctx, StateMachineBehaviour behaviour)
        {
            var calculation = (WNAE_ParameterCalculation)behaviour;
            var operation = calculation.operation;
            var usesB = WNAECalc.UsesB(operation);

            ctx.BitWidth = Mathf.Clamp(
                calculation.bitWidth, WNAECalc.MinBitWidth, WNAECalc.MaxBitWidth);

            WNAEBehaviourExpander.DeclareParameter(
                controller, calculation.parameterA, AnimatorControllerParameterType.Int, DisplayName);
            if (usesB)
            {
                WNAEBehaviourExpander.DeclareParameter(
                    controller, calculation.parameterB, AnimatorControllerParameterType.Int, DisplayName);
            }
            WNAEBehaviourExpander.DeclareParameter(
                controller, calculation.parameterC, AnimatorControllerParameterType.Int, DisplayName);

            // 中間パラメータは同期不要なので VRCExpressionParameters には追加しない。
            // 演算ごとに使うものだけ宣言する（Add / Sub は a を直接 acc に置くので ta 不要）
            var usesTa = operation != WNAECalcOperation.Add && operation != WNAECalcOperation.Sub;
            if (usesTa) WNAEAnimator.EnsureIntParameter(controller, ctx.Ta);
            if (usesB) WNAEAnimator.EnsureIntParameter(controller, ctx.Tb);
            WNAEAnimator.EnsureIntParameter(controller, ctx.Acc);
            if (operation == WNAECalcOperation.Mul) WNAEAnimator.EnsureIntParameter(controller, ctx.T2);
        }

        public WNAEBuildResult Build(WNAEChainContext ctx, StateMachineBehaviour behaviour)
        {
            return Build(ctx, (WNAE_ParameterCalculation)behaviour);
        }

        #region Validation

        public static List<WNAEIssue> Validate(WNAE_ParameterCalculation calculation)
        {
            var issues = new List<WNAEIssue>();

            if (string.IsNullOrWhiteSpace(calculation.parameterA))
            {
                issues.Add(new WNAEIssue(WNAEIssueLevel.Error, "Parameter A が指定されていません。"));
            }

            if (WNAECalc.UsesB(calculation.operation) && string.IsNullOrWhiteSpace(calculation.parameterB))
            {
                issues.Add(new WNAEIssue(WNAEIssueLevel.Error, "Parameter B が指定されていません。"));
            }

            if (string.IsNullOrWhiteSpace(calculation.parameterC))
            {
                issues.Add(new WNAEIssue(WNAEIssueLevel.Error, "Parameter C が指定されていません。"));
            }

            if (calculation.bitWidth < WNAECalc.MinBitWidth || calculation.bitWidth > WNAECalc.MaxBitWidth)
            {
                issues.Add(new WNAEIssue(WNAEIssueLevel.Error,
                    $"Bit Width は {WNAECalc.MinBitWidth}〜{WNAECalc.MaxBitWidth} で指定してください" +
                    $"（現在 {calculation.bitWidth}）。"));
            }

            var states = EstimateStateCount(calculation);
            if (states > 512)
            {
                issues.Add(new WNAEIssue(WNAEIssueLevel.Warning,
                    $"この設定では約 {states} State が生成されます。Bit Width を下げると大幅に減らせます。"));
            }

            return issues;
        }

        /// <summary>
        /// 生成される State 数。Init と Store を含む、実際の生成数と一致する値を返す。
        /// </summary>
        public static int EstimateStateCount(WNAE_ParameterCalculation calculation)
        {
            var n = Mathf.Clamp(calculation.bitWidth, WNAECalc.MinBitWidth, WNAECalc.MaxBitWidth);
            var values = 1 << n;

            switch (calculation.operation)
            {
                // Init + 展開 n + 桁あふれ補正 2 + Store
                case WNAECalcOperation.Add:
                case WNAECalcOperation.Sub:
                    return n + 4;

                // Init + 展開 n + Store
                case WNAECalcOperation.Not:
                    return n + 2;

                // Init + 4n + Store
                case WNAECalcOperation.And:
                case WNAECalcOperation.Or:
                case WNAECalcOperation.Xor:
                case WNAECalcOperation.Xnor:
                    return 4 * n + 2;

                // Init + 上位分岐 + 下位分岐 + 下位積の合算 + 合流 + 桁落とし n + Store
                case WNAECalcOperation.Mul:
                {
                    var low = n / 2;
                    var high = n - low;
                    var subSize = 1 << low;

                    var count = (1 << high) - 1 + subSize + n + 3;
                    if (subSize >= 2) count += FloorLog2((long)(values - 1) * (subSize - 1)) + 1;
                    return count;
                }

                // Init + 分岐 n + 受け皿 + 桁落とし (n-1) + Store
                case WNAECalcOperation.ShiftLeft:
                    return n >= 2 ? 2 * n + 2 : 4;

                // Init + Σ(分岐 + 展開 n-s) + 受け皿 + Store
                case WNAECalcOperation.ShiftRight:
                    return n * (n + 1) / 2 + n + 3;

                // Init + 剰余化 + 分岐 n + 分岐ごとに展開 n + 受け皿 + Store
                case WNAECalcOperation.RotateLeft:
                case WNAECalcOperation.RotateRight:
                {
                    var reduceHi = 0;
                    while ((long)n << (reduceHi + 1) <= values - 1) reduceHi++;
                    return n * n + n + reduceHi + 4;
                }

                // Init + 分岐 + 分岐ごとに商の展開 + ゼロ除算 + Store
                case WNAECalcOperation.Div:
                case WNAECalcOperation.Mod:
                {
                    var levels = 0;
                    for (var b = 1; b < values; b++)
                    {
                        var top = 0;
                        while ((long)b << (top + 1) <= values - 1) top++;
                        levels += top + 1;
                    }
                    return values + levels + 2;
                }

                default:
                    return 0;
            }
        }

        private static int FloorLog2(long value)
        {
            var result = 0;
            while (value >= 2)
            {
                value >>= 1;
                result++;
            }
            return result;
        }

        #endregion

        #region Chain construction

        private static WNAEBuildResult Build(WNAEChainContext ctx, WNAE_ParameterCalculation calculation)
        {
            var init = ctx.NewState("Init", 0, d => FillInit(d, ctx, calculation));
            var entry = new List<VirtualState> { init };

            VirtualState store;
            switch (calculation.operation)
            {
                case WNAECalcOperation.Add:
                    store = BuildAddSub(ctx, entry, calculation, subtract: false);
                    break;
                case WNAECalcOperation.Sub:
                    store = BuildAddSub(ctx, entry, calculation, subtract: true);
                    break;
                case WNAECalcOperation.Not:
                    store = BuildNot(ctx, entry, calculation);
                    break;
                case WNAECalcOperation.And:
                case WNAECalcOperation.Or:
                case WNAECalcOperation.Xor:
                case WNAECalcOperation.Xnor:
                    store = BuildBitwise(ctx, entry, calculation);
                    break;
                case WNAECalcOperation.Mul:
                    store = BuildMul(ctx, entry, calculation);
                    break;
                case WNAECalcOperation.ShiftLeft:
                    store = BuildShiftLeft(ctx, entry, calculation);
                    break;
                case WNAECalcOperation.ShiftRight:
                    store = BuildShiftRight(ctx, entry, calculation);
                    break;
                case WNAECalcOperation.RotateLeft:
                    store = BuildRotate(ctx, entry, calculation, right: false);
                    break;
                case WNAECalcOperation.RotateRight:
                    store = BuildRotate(ctx, entry, calculation, right: true);
                    break;
                default:
                    store = BuildDivMod(ctx, entry, calculation,
                        modulo: calculation.operation == WNAECalcOperation.Mod);
                    break;
            }

            return new WNAEBuildResult { First = init, Last = store };
        }

        private static void FillInit(
            VRCAvatarParameterDriver d, WNAEChainContext ctx, WNAE_ParameterCalculation calculation)
        {
            switch (calculation.operation)
            {
                case WNAECalcOperation.Add:
                case WNAECalcOperation.Sub:
                    // a は最初から acc に置く。ta は使わない
                    WNAEAnimator.AddCopy(d, ctx.Acc, calculation.parameterA);
                    WNAEAnimator.AddCopy(d, ctx.Tb, calculation.parameterB);
                    break;

                case WNAECalcOperation.Not:
                    // c = (2^N - 1) - a。全ビットの立った値から a の立っているビットを引いていく
                    WNAEAnimator.AddCopy(d, ctx.Ta, calculation.parameterA);
                    WNAEAnimator.AddSet(d, ctx.Acc, ctx.Modulus - 1);
                    break;

                case WNAECalcOperation.Mul:
                    WNAEAnimator.AddCopy(d, ctx.Ta, calculation.parameterA);
                    WNAEAnimator.AddCopy(d, ctx.Tb, calculation.parameterB);
                    WNAEAnimator.AddSet(d, ctx.Acc, 0f);
                    WNAEAnimator.AddSet(d, ctx.T2, 0f);
                    break;

                default:
                    WNAEAnimator.AddCopy(d, ctx.Ta, calculation.parameterA);
                    WNAEAnimator.AddCopy(d, ctx.Tb, calculation.parameterB);
                    WNAEAnimator.AddSet(d, ctx.Acc, 0f);
                    break;
            }
        }

        /// <summary>結果を c へ書き出す終端。Mod だけは商ではなく剰余（ta）が答え。</summary>
        private static VirtualState NewStore(WNAEChainContext ctx, WNAE_ParameterCalculation calculation)
        {
            var source = calculation.operation == WNAECalcOperation.Mod ? ctx.Ta : ctx.Acc;

            ctx.Column++;
            return ctx.NewState("Store", 0,
                d => WNAEAnimator.AddCopy(d, calculation.parameterC, source));
        }

        /// <summary>c = a ± b。tb の立っているビットを直接消しながら acc に足し引きする。</summary>
        private static VirtualState BuildAddSub(
            WNAEChainContext ctx, List<VirtualState> entry, WNAE_ParameterCalculation calculation,
            bool subtract)
        {
            var n = ctx.BitWidth;
            var modulus = ctx.Modulus;

            var sources = WNAEChain.GreedyDispatch(ctx, entry, ctx.Tb, n - 1, 0, 0,
                k => 1L << k,
                k => $"b{k}",
                k =>
                {
                    var bit = 1 << k;
                    return (Action<VRCAvatarParameterDriver>)(d =>
                    {
                        WNAEAnimator.AddAdd(d, ctx.Tb, -bit);
                        WNAEAnimator.AddAdd(d, ctx.Acc, subtract ? -bit : bit);
                    });
                });

            // 桁あふれ / 桁借りの補正。tb を消し切ったこと（tb < 1）を条件に含める
            ctx.Column++;
            var done = WNAEAnimator.Condition(AnimatorConditionMode.Less, ctx.Tb, 1);
            VirtualState wrap;
            var keep = default(VirtualState);

            if (subtract)
            {
                wrap = ctx.NewState("wrap+", 0, d => WNAEAnimator.AddAdd(d, ctx.Acc, modulus));
                keep = ctx.NewState("keep", 1, null);
                WNAEChain.Link(sources, wrap,
                    new[] { done, WNAEAnimator.Condition(AnimatorConditionMode.Less, ctx.Acc, 0) });
                WNAEChain.Link(sources, keep,
                    new[] { done, WNAEAnimator.Condition(AnimatorConditionMode.Greater, ctx.Acc, -1) });
            }
            else
            {
                wrap = ctx.NewState("wrap-", 0, d => WNAEAnimator.AddAdd(d, ctx.Acc, -modulus));
                keep = ctx.NewState("keep", 1, null);
                WNAEChain.Link(sources, wrap,
                    new[] { done, WNAEAnimator.Condition(AnimatorConditionMode.Greater, ctx.Acc, modulus - 1) });
                WNAEChain.Link(sources, keep,
                    new[] { done, WNAEAnimator.Condition(AnimatorConditionMode.Less, ctx.Acc, modulus) });
            }

            // b が値域外でも行き場が無くならないようにだけする（結果は保証しない）
            WNAEChain.Link(entry, keep,
                new[] { WNAEAnimator.Condition(AnimatorConditionMode.Greater, ctx.Tb, modulus - 1) });

            var store = NewStore(ctx, calculation);
            WNAEChain.Link(new[] { wrap, keep }, store, null);
            return store;
        }

        /// <summary>c = ~a。acc = 2^N - 1 から、a の立っているビットを引いていく。</summary>
        private static VirtualState BuildNot(
            WNAEChainContext ctx, List<VirtualState> entry, WNAE_ParameterCalculation calculation)
        {
            var n = ctx.BitWidth;
            var modulus = ctx.Modulus;

            var sources = WNAEChain.GreedyDispatch(ctx, entry, ctx.Ta, n - 1, 0, 0,
                k => 1L << k,
                k => $"b{k}",
                k =>
                {
                    var bit = 1 << k;
                    return (Action<VRCAvatarParameterDriver>)(d =>
                    {
                        WNAEAnimator.AddAdd(d, ctx.Ta, -bit);
                        WNAEAnimator.AddAdd(d, ctx.Acc, -bit);
                    });
                });

            var store = NewStore(ctx, calculation);
            WNAEChain.Link(sources, store,
                new[] { WNAEAnimator.Condition(AnimatorConditionMode.Less, ctx.Ta, 1) });

            // a が値域外でも行き場が無くならないようにだけする
            WNAEChain.Link(entry, store,
                new[] { WNAEAnimator.Condition(AnimatorConditionMode.Greater, ctx.Ta, modulus - 1) });

            return store;
        }

        /// <summary>
        /// ta と tb を同時に崩し、ビットごとの論理演算結果を acc へ積む。
        ///
        /// 2 つの値の「どちらかにビットが立っている最上位」は範囲条件 2 つでは表せないため、
        /// ここだけは全レベルを順に通る 4 分岐（(a,b) の全組み合わせ）のままにしている。
        /// レベルを飛ばす形にすると State は 3n+2 に減るが、遷移が約 2 倍以上に膨らむ。
        /// </summary>
        private static VirtualState BuildBitwise(
            WNAEChainContext ctx, List<VirtualState> entry, WNAE_ParameterCalculation calculation)
        {
            var operation = calculation.operation;
            var current = entry;

            for (var k = ctx.BitWidth - 1; k >= 0; k--)
            {
                var bit = 1 << k;
                ctx.Column++;

                var next = new List<VirtualState>();

                // (a, b) の 4 通り
                var cases = new[]
                {
                    new { A = true, B = true, Row = 0 },
                    new { A = true, B = false, Row = 1 },
                    new { A = false, B = true, Row = 2 },
                    new { A = false, B = false, Row = 3 },
                };

                foreach (var c in cases)
                {
                    var aBit = c.A;
                    var bBit = c.B;
                    var result = Apply(operation, aBit, bBit);

                    var state = ctx.NewState($"b{k}={(aBit ? 1 : 0)}{(bBit ? 1 : 0)}", c.Row, d =>
                    {
                        if (aBit) WNAEAnimator.AddAdd(d, ctx.Ta, -bit);
                        if (bBit) WNAEAnimator.AddAdd(d, ctx.Tb, -bit);
                        if (result) WNAEAnimator.AddAdd(d, ctx.Acc, bit);
                    });

                    WNAEChain.Link(current, state, new[]
                    {
                        aBit ? WNAEChain.BitSet(ctx.Ta, bit) : WNAEChain.BitClear(ctx.Ta, bit),
                        bBit ? WNAEChain.BitSet(ctx.Tb, bit) : WNAEChain.BitClear(ctx.Tb, bit),
                    });
                    next.Add(state);
                }

                current = next;
            }

            var store = NewStore(ctx, calculation);
            WNAEChain.Link(current, store, null);
            return store;
        }

        private static bool Apply(WNAECalcOperation operation, bool a, bool b)
        {
            switch (operation)
            {
                case WNAECalcOperation.And: return a && b;
                case WNAECalcOperation.Or: return a || b;
                case WNAECalcOperation.Xor: return a ^ b;
                case WNAECalcOperation.Xnor: return a == b;
                default: return false;
            }
        }

        /// <summary>
        /// c = a * b。b を上位 / 下位の 2 ブロックに割り、それぞれ定数倍（Convert Range）で
        /// 部分積を作ってから足し合わせる。全値分岐（2^N 個）に比べ State 数が桁違いに少ない。
        ///
        /// Convert Range の変換元は 0〜2^N にしている。2 の冪なので除算が浮動小数点でも厳密で、
        /// 入力が変換元範囲に収まるため、クランプの有無にかかわらず結果が変わらない。
        /// 変換先は 0〜2^N×定数の整数傾きなので、結果も厳密な整数になる。
        /// </summary>
        private static VirtualState BuildMul(
            WNAEChainContext ctx, List<VirtualState> entry, WNAE_ParameterCalculation calculation)
        {
            var n = ctx.BitWidth;
            var modulus = ctx.Modulus;
            var low = n / 2;
            var high = n - low;
            var subSize = 1 << low;

            // 上位ブロック: tb ∈ [m×S, (m+1)×S) で分岐し、acc = a × (m×S) を作る。
            // m = 0 は掛ける定数が 0（acc は Init の 0 のまま）なので分岐を作らない
            ctx.Column++;
            var lowEntry = new List<VirtualState>(entry);

            for (var m = 1; m < 1 << high; m++)
            {
                var scaled = m * subSize;
                var state = ctx.NewState($"acc=a×{scaled}", m - 1, d =>
                {
                    WNAEAnimator.AddCopyRange(
                        d, ctx.Acc, ctx.Ta, 0f, modulus, 0f, (float)modulus * scaled);
                    WNAEAnimator.AddAdd(d, ctx.Tb, -scaled);
                });

                WNAEChain.Link(entry, state, new[]
                {
                    WNAEAnimator.Condition(AnimatorConditionMode.Greater, ctx.Tb, scaled - 1),
                    WNAEAnimator.Condition(AnimatorConditionMode.Less, ctx.Tb, scaled + subSize),
                });
                lowEntry.Add(state);
            }

            // 下位ブロック: 残った tb = l で分岐し、t2 = a × l を作る
            ctx.Column++;
            var addEntry = new List<VirtualState>();

            for (var l = 0; l < subSize; l++)
            {
                var factor = l;
                var state = ctx.NewState($"t2=a×{factor}", factor, factor == 0
                    ? (Action<VRCAvatarParameterDriver>)null
                    : d => WNAEAnimator.AddCopyRange(
                        d, ctx.T2, ctx.Ta, 0f, modulus, 0f, (float)modulus * factor));

                WNAEChain.Link(lowEntry, state,
                    new[] { WNAEAnimator.Condition(AnimatorConditionMode.Equals, ctx.Tb, factor) });
                addEntry.Add(state);
            }

            // 下位の積 t2 を acc に足し込む
            var sources = addEntry;
            var t2Hi = 0;

            if (subSize >= 2)
            {
                t2Hi = FloorLog2((long)(modulus - 1) * (subSize - 1));

                sources = WNAEChain.GreedyDispatch(ctx, addEntry, ctx.T2, t2Hi, 0, 0,
                    k => 1L << k,
                    k => $"t2:b{k}",
                    k =>
                    {
                        var bit = 1 << k;
                        return (Action<VRCAvatarParameterDriver>)(d =>
                        {
                            WNAEAnimator.AddAdd(d, ctx.T2, -bit);
                            WNAEAnimator.AddAdd(d, ctx.Acc, bit);
                        });
                    });
            }

            // 合流点。t2 を足し切ってから桁落としへ進む
            ctx.Column++;
            var join = ctx.NewState("join", 0, null);
            WNAEChain.Link(sources, join,
                new[] { WNAEAnimator.Condition(AnimatorConditionMode.Less, ctx.T2, 1) });

            if (subSize >= 2)
            {
                // a が値域外だった場合の受け皿
                WNAEChain.Link(addEntry, join, new[]
                {
                    WNAEAnimator.Condition(
                        AnimatorConditionMode.Greater, ctx.T2, (1L << (t2Hi + 1)) - 1),
                });
            }

            // 積は最大 (2^N-1)² なので、acc の bit 2N-1 … N を落として mod 2^N にする
            var stripSources = WNAEChain.GreedyDispatch(
                ctx, new List<VirtualState> { join }, ctx.Acc, 2 * n - 1, n, 0,
                k => 1L << k,
                k => $"acc:b{k}",
                k =>
                {
                    var bit = 1 << k;
                    return (Action<VRCAvatarParameterDriver>)(d => WNAEAnimator.AddAdd(d, ctx.Acc, -bit));
                });

            var store = NewStore(ctx, calculation);
            WNAEChain.Link(stripSources, store,
                new[] { WNAEAnimator.Condition(AnimatorConditionMode.Less, ctx.Acc, modulus) });

            // a が値域外だった場合の受け皿
            WNAEChain.Link(new[] { join }, store, new[]
            {
                WNAEAnimator.Condition(AnimatorConditionMode.Greater, ctx.Acc, (1L << (2 * n)) - 1),
            });

            // b が値域外だった場合の受け皿
            WNAEChain.LinkOutOfRange(entry, store, ctx.Tb, 0, modulus - 1);

            return store;
        }

        /// <summary>c = (a &lt;&lt; b) &amp; mask。シフト量ごとに定数倍してから上位ビットを落とす。</summary>
        private static VirtualState BuildShiftLeft(
            WNAEChainContext ctx, List<VirtualState> entry, WNAE_ParameterCalculation calculation)
        {
            var n = ctx.BitWidth;
            var modulus = ctx.Modulus;

            ctx.Column++;
            var branches = new List<VirtualState>();

            for (var s = 0; s < n; s++)
            {
                var shift = 1 << s;
                var state = ctx.NewState($"acc=a×{shift}", s,
                    d => WNAEAnimator.AddCopyRange(
                        d, ctx.Acc, ctx.Ta, 0f, modulus, 0f, (float)modulus * shift));

                WNAEChain.Link(entry, state,
                    new[] { WNAEAnimator.Condition(AnimatorConditionMode.Equals, ctx.Tb, s) });
                branches.Add(state);
            }

            // シフト量がビット幅以上なら全ビットが押し出される（acc は 0 のまま）
            var fallback = ctx.NewState("s≥N", n, null);
            WNAEChain.LinkOutOfRange(entry, fallback, ctx.Tb, 0, n - 1);
            branches.Add(fallback);

            // 積は最大 (2^N-1)×2^(N-1) なので bit 2N-2 … N を落とす
            var sources = branches;
            if (n >= 2)
            {
                sources = WNAEChain.GreedyDispatch(ctx, branches, ctx.Acc, 2 * n - 2, n, 0,
                    k => 1L << k,
                    k => $"acc:b{k}",
                    k =>
                    {
                        var bit = 1 << k;
                        return (Action<VRCAvatarParameterDriver>)(d => WNAEAnimator.AddAdd(d, ctx.Acc, -bit));
                    });
            }

            var store = NewStore(ctx, calculation);
            WNAEChain.Link(sources, store,
                new[] { WNAEAnimator.Condition(AnimatorConditionMode.Less, ctx.Acc, modulus) });

            // a が値域外だった場合の受け皿
            var junkLimit = n >= 2 ? (1L << (2 * n - 1)) - 1 : modulus - 1;
            WNAEChain.Link(branches, store,
                new[] { WNAEAnimator.Condition(AnimatorConditionMode.Greater, ctx.Acc, junkLimit) });

            return store;
        }

        /// <summary>
        /// c = a &gt;&gt; b。シフト量ごとに分岐し、ta の立っているビットを消しながら
        /// acc に 2^(k-s) を積む。bit s 未満が残ったら打ち切る（自然に切り捨てられる）。
        /// </summary>
        private static VirtualState BuildShiftRight(
            WNAEChainContext ctx, List<VirtualState> entry, WNAE_ParameterCalculation calculation)
        {
            var n = ctx.BitWidth;
            var modulus = ctx.Modulus;

            ctx.Column++;
            var dispatchColumn = ctx.Column;
            var maxColumn = ctx.Column;
            var pending = new List<(List<VirtualState> Sources, VirtualState Branch, int DoneLimit)>();

            for (var s = 0; s < n; s++)
            {
                ctx.Column = dispatchColumn;

                var shift = s;
                var branch = ctx.NewState($"s={s}", s, null);
                WNAEChain.Link(entry, branch,
                    new[] { WNAEAnimator.Condition(AnimatorConditionMode.Equals, ctx.Tb, s) });

                var sources = WNAEChain.GreedyDispatch(
                    ctx, new List<VirtualState> { branch }, ctx.Ta, n - 1, s, s,
                    k => 1L << k,
                    k => $"b{k}",
                    k =>
                    {
                        var bit = 1 << k;
                        var weight = 1 << (k - shift);
                        return (Action<VRCAvatarParameterDriver>)(d =>
                        {
                            WNAEAnimator.AddAdd(d, ctx.Ta, -bit);
                            WNAEAnimator.AddAdd(d, ctx.Acc, weight);
                        });
                    });

                pending.Add((sources, branch, 1 << s));
                maxColumn = Mathf.Max(maxColumn, ctx.Column);
            }

            // シフト量がビット幅以上なら c = 0（acc は Init の 0 のまま）
            ctx.Column = dispatchColumn;
            var fallback = ctx.NewState("s≥N", n, null);
            WNAEChain.LinkOutOfRange(entry, fallback, ctx.Tb, 0, n - 1);

            ctx.Column = maxColumn;
            var store = NewStore(ctx, calculation);

            foreach (var (sources, branch, doneLimit) in pending)
            {
                WNAEChain.Link(sources, store,
                    new[] { WNAEAnimator.Condition(AnimatorConditionMode.Less, ctx.Ta, doneLimit) });

                // a が値域外だった場合の受け皿
                WNAEChain.Link(new[] { branch }, store,
                    new[] { WNAEAnimator.Condition(AnimatorConditionMode.Greater, ctx.Ta, modulus - 1) });
            }

            WNAEChain.Link(new[] { fallback }, store, null);
            return store;
        }

        /// <summary>
        /// c = a を b ビット回転（L-ROTATE / R-ROTATE）。押し出されたビットは反対側へ回り込む。
        ///
        /// 回転は N ビットで一周するため、まず tb から N×2^k を引けるだけ引いて tb mod N に
        /// 落としてから、N 個の分岐で回転量ごとのビット載せ替えを行う。
        /// </summary>
        private static VirtualState BuildRotate(
            WNAEChainContext ctx, List<VirtualState> entry, WNAE_ParameterCalculation calculation,
            bool right)
        {
            var n = ctx.BitWidth;
            var modulus = ctx.Modulus;

            // tb mod N へ落とす（引く量は常に N の倍数なので剰余は変わらない）
            var reduceHi = 0;
            while ((long)n << (reduceHi + 1) <= modulus - 1) reduceHi++;

            var reduced = WNAEChain.GreedyDispatch(ctx, entry, ctx.Tb, reduceHi, 0, 0,
                k => (long)n << k,
                k => $"-{n << k}",
                k =>
                {
                    var step = n << k;
                    return (Action<VRCAvatarParameterDriver>)(d => WNAEAnimator.AddAdd(d, ctx.Tb, -step));
                });

            ctx.Column++;
            var dispatchColumn = ctx.Column;
            var maxColumn = ctx.Column;
            var pending = new List<(List<VirtualState> Sources, VirtualState Branch)>();

            for (var s = 0; s < n; s++)
            {
                ctx.Column = dispatchColumn;

                var rotation = s;
                var branch = ctx.NewState($"s={s}", s, null);
                WNAEChain.Link(reduced, branch,
                    new[] { WNAEAnimator.Condition(AnimatorConditionMode.Equals, ctx.Tb, s) });

                var sources = WNAEChain.GreedyDispatch(
                    ctx, new List<VirtualState> { branch }, ctx.Ta, n - 1, 0, s,
                    k => 1L << k,
                    k => $"b{k}",
                    k =>
                    {
                        var bit = 1 << k;

                        // 回転後の桁。範囲外へ出た分は反対側へ回り込む
                        var destination = right ? (k - rotation + n) % n : (k + rotation) % n;
                        var weight = 1 << destination;

                        return (Action<VRCAvatarParameterDriver>)(d =>
                        {
                            WNAEAnimator.AddAdd(d, ctx.Ta, -bit);
                            WNAEAnimator.AddAdd(d, ctx.Acc, weight);
                        });
                    });

                pending.Add((sources, branch));
                maxColumn = Mathf.Max(maxColumn, ctx.Column);
            }

            // b が値域外なら c = 0（acc は Init の 0 のまま）
            ctx.Column = dispatchColumn;
            var fallback = ctx.NewState("b=out", n, null);
            WNAEChain.LinkOutOfRange(entry, fallback, ctx.Tb, 0, ((long)n << (reduceHi + 1)) - 1);

            ctx.Column = maxColumn;
            var store = NewStore(ctx, calculation);

            foreach (var (sources, branch) in pending)
            {
                WNAEChain.Link(sources, store,
                    new[] { WNAEAnimator.Condition(AnimatorConditionMode.Less, ctx.Ta, 1) });

                // a が値域外だった場合の受け皿
                WNAEChain.Link(new[] { branch }, store,
                    new[] { WNAEAnimator.Condition(AnimatorConditionMode.Greater, ctx.Ta, modulus - 1) });
            }

            WNAEChain.Link(new[] { fallback }, store, null);
            return store;
        }

        /// <summary>
        /// c = a / b（Div）または a % b（Mod）。
        /// tb の値ごとに分岐すると除数が定数になるので、b×2^k を引けるだけ引く二分長除算に落とせる。
        /// 商が acc に、剰余が ta に残るため、同じチェーンで両方求まる。
        /// </summary>
        private static VirtualState BuildDivMod(
            WNAEChainContext ctx, List<VirtualState> entry, WNAE_ParameterCalculation calculation,
            bool modulo)
        {
            var modulus = ctx.Modulus;
            var max = modulus - 1;

            ctx.Column++;
            var dispatchColumn = ctx.Column;
            var maxColumn = ctx.Column;
            var pending = new List<(List<VirtualState> Sources, VirtualState Branch, int Divisor, long JunkLimit)>();

            for (var b = 1; b < modulus; b++)
            {
                ctx.Column = dispatchColumn;

                var divisor = b;
                var branch = ctx.NewState($"b={b}", b - 1, null);
                WNAEChain.Link(entry, branch,
                    new[] { WNAEAnimator.Condition(AnimatorConditionMode.Equals, ctx.Tb, divisor) });

                // b×2^k が最大値を超えない範囲の k から始める
                var top = 0;
                while ((long)divisor << (top + 1) <= max) top++;

                var sources = WNAEChain.GreedyDispatch(
                    ctx, new List<VirtualState> { branch }, ctx.Ta, top, 0, b - 1,
                    k => (long)divisor << k,
                    k => $"-{divisor << k}",
                    k =>
                    {
                        var step = divisor << k;
                        var weight = 1 << k;
                        return (Action<VRCAvatarParameterDriver>)(d =>
                        {
                            WNAEAnimator.AddAdd(d, ctx.Ta, -step);
                            WNAEAnimator.AddAdd(d, ctx.Acc, weight);
                        });
                    });

                pending.Add((sources, branch, divisor, ((long)divisor << (top + 1)) - 1));
                maxColumn = Mathf.Max(maxColumn, ctx.Column);
            }

            // b = 0 と値域外はゼロ除算として c = 0 にする
            ctx.Column = dispatchColumn;
            var zero = ctx.NewState("b=0", modulus - 1, d =>
            {
                WNAEAnimator.AddSet(d, ctx.Acc, 0f);
                if (modulo) WNAEAnimator.AddSet(d, ctx.Ta, 0f);
            });
            WNAEChain.LinkOutOfRange(entry, zero, ctx.Tb, 1, max);

            ctx.Column = maxColumn;
            var store = NewStore(ctx, calculation);

            foreach (var (sources, branch, divisor, junkLimit) in pending)
            {
                // 残りが除数を下回ったら商が確定（剰余は ta に残る）
                WNAEChain.Link(sources, store,
                    new[] { WNAEAnimator.Condition(AnimatorConditionMode.Less, ctx.Ta, divisor) });

                // a が値域外だった場合の受け皿
                WNAEChain.Link(new[] { branch }, store,
                    new[] { WNAEAnimator.Condition(AnimatorConditionMode.Greater, ctx.Ta, junkLimit) });
            }

            WNAEChain.Link(new[] { zero }, store, null);
            return store;
        }

        #endregion
    }

    #endregion

    #region Inspector

    [CustomEditor(typeof(WNAE_ParameterCalculation))]
    internal class WNAE_ParameterCalculationInspector : WNAEBehaviourInspector
    {
        private static readonly string[] OperationLabels =
            Enum.GetValues(typeof(WNAECalcOperation))
                .Cast<WNAECalcOperation>()
                .Select(WNAECalc.DisplayName)
                .ToArray();

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            var operationProp = serializedObject.FindProperty("operation");
            operationProp.enumValueIndex = EditorGUILayout.Popup(
                new GUIContent("Operation"), operationProp.enumValueIndex, OperationLabels);

            var operation = (WNAECalcOperation)operationProp.enumValueIndex;

            DrawParameterRow("A (入力)", serializedObject.FindProperty("parameterA"),
                IntParameters, "（未選択）");

            using (new EditorGUI.DisabledScope(!WNAECalc.UsesB(operation)))
            {
                DrawParameterRow("B (入力)", serializedObject.FindProperty("parameterB"),
                    IntParameters, "（未選択）");
            }

            DrawParameterRow("C (出力)", serializedObject.FindProperty("parameterC"),
                IntParameters, "（未選択）");

            EditorGUILayout.PropertyField(
                serializedObject.FindProperty("bitWidth"),
                new GUIContent("Bit Width",
                    "演算のビット幅。生成される State 数を大きく左右するので、実際に使う値域に合わせて絞ってください。"));

            serializedObject.ApplyModifiedProperties();

            // Bit Width を決めるための唯一の判断材料なので、これだけは表示する
            var calculation = (WNAE_ParameterCalculation)target;
            EditorGUILayout.LabelField(
                $"生成 State 数: {WNAECalcBuilder.EstimateStateCount(calculation)}",
                EditorStyles.miniLabel);

            DrawErrors(WNAECalcBuilder.Validate(calculation));
            DrawRefreshButton();
        }
    }

    #endregion
}
