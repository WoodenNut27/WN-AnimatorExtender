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
    #region Encoder

    /// <summary>
    /// 8 個の Bool を 1 個の Int へエンコードする。
    /// 固定ビットは Init で定数として一括加算し、パラメータのビットだけ 2 分岐を作る。
    /// Bool には Int のような範囲条件が使えないため、レベルを飛ばす最適化はできない。
    /// </summary>
    internal class WNAEEncoderBuilder : IWNAEBehaviourBuilder
    {
        public static readonly WNAEEncoderBuilder Instance = new WNAEEncoderBuilder();

        public string DisplayName => "Parameter Encoder";
        public string ParameterPrefix => "WNAE/Encoder/";
        public string SubStateMachinePrefix => "WNAE Encoder/";

        public bool Matches(StateMachineBehaviour behaviour) => behaviour is WNAE_ParameterEncoder;

        public List<WNAEIssue> Validate(StateMachineBehaviour behaviour)
        {
            var issues = new List<WNAEIssue>();
            var encoder = (WNAE_ParameterEncoder)behaviour;

            if (string.IsNullOrWhiteSpace(encoder.outputParameter))
            {
                issues.Add(new WNAEIssue(WNAEIssueLevel.Error, "出力 Int パラメータが指定されていません。"));
            }

            var bits = encoder.NormalizedBits();
            for (var i = 0; i < bits.Length; i++)
            {
                if (bits[i].source != WNAEBitSource.Parameter) continue;

                if (string.IsNullOrWhiteSpace(bits[i].parameterName))
                {
                    issues.Add(new WNAEIssue(WNAEIssueLevel.Error,
                        $"{WNAECodec.BitLabel(i)} が Bool パラメータ指定になっていますが、パラメータが選ばれていません。"));
                }
            }

            return issues;
        }

        public IEnumerable<KeyValuePair<string, AnimatorControllerParameterType>> RequiredParameters(
            StateMachineBehaviour behaviour)
        {
            var encoder = (WNAE_ParameterEncoder)behaviour;

            yield return new KeyValuePair<string, AnimatorControllerParameterType>(
                encoder.outputParameter, AnimatorControllerParameterType.Int);

            foreach (var bit in encoder.NormalizedBits())
            {
                if (bit.source != WNAEBitSource.Parameter) continue;

                yield return new KeyValuePair<string, AnimatorControllerParameterType>(
                    bit.parameterName, AnimatorControllerParameterType.Bool);
            }
        }

        public void Prepare(
            VirtualAnimatorController controller, WNAEChainContext ctx, StateMachineBehaviour behaviour)
        {
            var encoder = (WNAE_ParameterEncoder)behaviour;

            WNAEBehaviourExpander.DeclareParameter(
                controller, encoder.outputParameter, AnimatorControllerParameterType.Int, DisplayName);

            foreach (var bit in encoder.NormalizedBits())
            {
                if (bit.source != WNAEBitSource.Parameter) continue;

                WNAEBehaviourExpander.DeclareParameter(
                    controller, bit.parameterName, AnimatorControllerParameterType.Bool, DisplayName);
            }

            // 中間パラメータは同期不要なので VRCExpressionParameters には追加しない
            WNAEAnimator.EnsureIntParameter(controller, ctx.Acc);
        }

        public WNAEBuildResult Build(WNAEChainContext ctx, StateMachineBehaviour behaviour)
        {
            var encoder = (WNAE_ParameterEncoder)behaviour;
            var bits = encoder.NormalizedBits();

            // 1 固定のビットは分岐が要らないので、Init で定数としてまとめて入れる
            var constant = 0;
            for (var i = 0; i < bits.Length; i++)
            {
                if (bits[i].source == WNAEBitSource.ConstantOne) constant |= 1 << i;
            }

            var init = ctx.NewState("Init", 0, d => WNAEAnimator.AddSet(d, ctx.Acc, constant));
            var current = new List<VirtualState> { init };

            for (var i = 0; i < bits.Length; i++)
            {
                if (bits[i].source != WNAEBitSource.Parameter) continue;

                var weight = 1 << i;
                var parameter = bits[i].parameterName;

                current = WNAEChain.BoolLevel(ctx, current, parameter, $"b{i}", 0,
                    d => WNAEAnimator.AddAdd(d, ctx.Acc, weight),
                    null);
            }

            ctx.Column++;
            var store = ctx.NewState("Store", 0,
                d => WNAEAnimator.AddCopy(d, encoder.outputParameter, ctx.Acc));
            WNAEChain.Link(current, store, null);

            return new WNAEBuildResult { First = init, Last = store };
        }

        /// <summary>生成される State 数。Init と Store を含む実際の生成数と一致する。</summary>
        public static int EstimateStateCount(WNAE_ParameterEncoder encoder)
        {
            var levels = encoder.NormalizedBits().Count(b => b.source == WNAEBitSource.Parameter);
            return 2 * levels + 2;
        }
    }

    #endregion

    #region Decoder

    /// <summary>
    /// 1 個の Int を 8 個の Bool へデコードする。
    /// Init で選択された出力を全部 0 にしておき、立っているビットを上位から直接消しながら
    /// 対応する出力だけ 1 にする。選択されていないビットに専用の State は生じない。
    /// </summary>
    internal class WNAEDecoderBuilder : IWNAEBehaviourBuilder
    {
        public static readonly WNAEDecoderBuilder Instance = new WNAEDecoderBuilder();

        public string DisplayName => "Parameter Decoder";
        public string ParameterPrefix => "WNAE/Decoder/";
        public string SubStateMachinePrefix => "WNAE Decoder/";

        public bool Matches(StateMachineBehaviour behaviour) => behaviour is WNAE_ParameterDecoder;

        public List<WNAEIssue> Validate(StateMachineBehaviour behaviour)
        {
            var issues = new List<WNAEIssue>();
            var decoder = (WNAE_ParameterDecoder)behaviour;

            if (string.IsNullOrWhiteSpace(decoder.inputParameter))
            {
                issues.Add(new WNAEIssue(WNAEIssueLevel.Error, "入力 Int パラメータが指定されていません。"));
            }

            if (decoder.LowestSelectedBit() < 0)
            {
                issues.Add(new WNAEIssue(WNAEIssueLevel.Error,
                    "出力先の Bool パラメータが 1 つも選択されていません。"));
            }

            // 同じ Bool を複数ビットに割り当てると、どれかのビットが立っていれば 1（OR）になる
            var bits = decoder.NormalizedBits();
            var seen = new Dictionary<string, int>();

            for (var i = 0; i < bits.Length; i++)
            {
                if (string.IsNullOrEmpty(bits[i])) continue;

                if (seen.TryGetValue(bits[i], out var previous))
                {
                    issues.Add(new WNAEIssue(WNAEIssueLevel.Warning,
                        $"\"{bits[i]}\" が {WNAECodec.BitLabel(previous)} と {WNAECodec.BitLabel(i)} の" +
                        "両方に割り当てられています。出力はどちらかのビットが立っていれば 1 になります。"));
                }
                else
                {
                    seen[bits[i]] = i;
                }
            }

            return issues;
        }

        public IEnumerable<KeyValuePair<string, AnimatorControllerParameterType>> RequiredParameters(
            StateMachineBehaviour behaviour)
        {
            var decoder = (WNAE_ParameterDecoder)behaviour;

            yield return new KeyValuePair<string, AnimatorControllerParameterType>(
                decoder.inputParameter, AnimatorControllerParameterType.Int);

            foreach (var name in decoder.NormalizedBits())
            {
                if (string.IsNullOrEmpty(name)) continue;

                yield return new KeyValuePair<string, AnimatorControllerParameterType>(
                    name, AnimatorControllerParameterType.Bool);
            }
        }

        public void Prepare(
            VirtualAnimatorController controller, WNAEChainContext ctx, StateMachineBehaviour behaviour)
        {
            var decoder = (WNAE_ParameterDecoder)behaviour;

            WNAEBehaviourExpander.DeclareParameter(
                controller, decoder.inputParameter, AnimatorControllerParameterType.Int, DisplayName);

            foreach (var name in decoder.NormalizedBits())
            {
                if (string.IsNullOrEmpty(name)) continue;

                WNAEBehaviourExpander.DeclareParameter(
                    controller, name, AnimatorControllerParameterType.Bool, DisplayName);
            }

            // 中間パラメータは同期不要なので VRCExpressionParameters には追加しない
            WNAEAnimator.EnsureIntParameter(controller, ctx.Ta);
        }

        public WNAEBuildResult Build(WNAEChainContext ctx, StateMachineBehaviour behaviour)
        {
            var decoder = (WNAE_ParameterDecoder)behaviour;
            var bits = decoder.NormalizedBits();
            var lowest = decoder.LowestSelectedBit();

            var init = ctx.NewState("Init", 0, d =>
            {
                WNAEAnimator.AddCopy(d, ctx.Ta, decoder.inputParameter);

                // 選択された出力は一旦 0 にしておき、立っているビットだけ後から 1 にする
                foreach (var target in bits)
                {
                    if (!string.IsNullOrEmpty(target)) WNAEAnimator.AddSet(d, target, 0f);
                }
            });

            var entry = new List<VirtualState> { init };

            // 選択された最下位ビットまで分解すれば足りる
            var sources = WNAEChain.GreedyDispatch(
                ctx, entry, ctx.Ta, WNAECodec.BitCount - 1, lowest, 0,
                k => 1L << k,
                k => $"b{k}",
                k =>
                {
                    var bit = 1 << k;
                    var target = bits[k];
                    var selected = !string.IsNullOrEmpty(target);

                    return (Action<VRCAvatarParameterDriver>)(d =>
                    {
                        WNAEAnimator.AddAdd(d, ctx.Ta, -bit);
                        if (selected) WNAEAnimator.AddSet(d, target, 1f);
                    });
                });

            // 出遷移の付け替え先を 1 つにするための終端。デコード完了後にここへ集まる
            ctx.Column++;
            var done = ctx.NewState("Done", 0, null);
            WNAEChain.Link(sources, done,
                new[] { WNAEAnimator.Condition(AnimatorConditionMode.Less, ctx.Ta, 1 << lowest) });

            // 入力が 2^8 以上でも行き場が無くならないようにだけする（結果は保証しない）
            WNAEChain.Link(entry, done, new[]
            {
                WNAEAnimator.Condition(
                    AnimatorConditionMode.Greater, ctx.Ta, (1 << WNAECodec.BitCount) - 1),
            });

            return new WNAEBuildResult { First = init, Last = done };
        }

        /// <summary>生成される State 数。Init と Done を含む実際の生成数と一致する。</summary>
        public static int EstimateStateCount(WNAE_ParameterDecoder decoder)
        {
            var lowest = decoder.LowestSelectedBit();
            if (lowest < 0) return 0;

            return WNAECodec.BitCount - lowest + 2;
        }
    }

    #endregion

    #region Inspectors

    [CustomEditor(typeof(WNAE_ParameterEncoder))]
    internal class WNAE_ParameterEncoderInspector : WNAEBehaviourInspector
    {
        private const string ZeroLabel = "0 固定";
        private const string OneLabel = "1 固定";

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            var encoder = (WNAE_ParameterEncoder)target;

            DrawParameterRow("出力 (Int)", serializedObject.FindProperty("outputParameter"),
                IntParameters, "（未選択）");

            EditorGUILayout.Space();

            var bitsProperty = serializedObject.FindProperty("bits");
            if (bitsProperty.arraySize != WNAECodec.BitCount) bitsProperty.arraySize = WNAECodec.BitCount;

            // 定数 2 種のあとに Bool パラメータを並べる
            var labels = new List<string> { ZeroLabel, OneLabel };
            labels.AddRange(BoolParameters);

            for (var i = 0; i < WNAECodec.BitCount; i++)
            {
                var element = bitsProperty.GetArrayElementAtIndex(i);
                var sourceProperty = element.FindPropertyRelative("source");
                var nameProperty = element.FindPropertyRelative("parameterName");

                var source = (WNAEBitSource)sourceProperty.intValue;
                string display;
                if (source == WNAEBitSource.ConstantZero) display = ZeroLabel;
                else if (source == WNAEBitSource.ConstantOne) display = OneLabel;
                else display = string.IsNullOrEmpty(nameProperty.stringValue)
                    ? "（未選択）"
                    : nameProperty.stringValue;

                var sourcePath = sourceProperty.propertyPath;
                var namePath = nameProperty.propertyPath;
                var so = serializedObject;

                DrawDropdownRow(WNAECodec.BitLabel(i), display, labels, index =>
                {
                    so.Update();
                    var sp = so.FindProperty(sourcePath);
                    var np = so.FindProperty(namePath);
                    if (sp == null || np == null) return;

                    if (index == 0)
                    {
                        sp.intValue = (int)WNAEBitSource.ConstantZero;
                    }
                    else if (index == 1)
                    {
                        sp.intValue = (int)WNAEBitSource.ConstantOne;
                    }
                    else
                    {
                        sp.intValue = (int)WNAEBitSource.Parameter;
                        np.stringValue = labels[index];
                    }

                    so.ApplyModifiedProperties();
                });
            }

            serializedObject.ApplyModifiedProperties();

            EditorGUILayout.Space();
            EditorGUILayout.LabelField(
                $"生成 State 数: {WNAEEncoderBuilder.EstimateStateCount(encoder)}", EditorStyles.miniLabel);

            DrawErrors(WNAEEncoderBuilder.Instance.Validate(encoder));
            DrawRefreshButton();
        }
    }

    [CustomEditor(typeof(WNAE_ParameterDecoder))]
    internal class WNAE_ParameterDecoderInspector : WNAEBehaviourInspector
    {
        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            var decoder = (WNAE_ParameterDecoder)target;

            DrawParameterRow("入力 (Int)", serializedObject.FindProperty("inputParameter"),
                IntParameters, "（未選択）");

            EditorGUILayout.Space();

            var bitsProperty = serializedObject.FindProperty("bits");
            if (bitsProperty.arraySize != WNAECodec.BitCount) bitsProperty.arraySize = WNAECodec.BitCount;

            for (var i = 0; i < WNAECodec.BitCount; i++)
            {
                DrawParameterRow(
                    WNAECodec.BitLabel(i),
                    bitsProperty.GetArrayElementAtIndex(i),
                    BoolParameters,
                    "（非選択）");
            }

            serializedObject.ApplyModifiedProperties();

            EditorGUILayout.Space();
            EditorGUILayout.LabelField(
                $"生成 State 数: {WNAEDecoderBuilder.EstimateStateCount(decoder)}", EditorStyles.miniLabel);

            DrawErrors(WNAEDecoderBuilder.Instance.Validate(decoder));
            DrawRefreshButton();
        }
    }

    #endregion
}
