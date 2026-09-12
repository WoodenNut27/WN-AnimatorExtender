using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using nadena.dev.ndmf.animator;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;

namespace WoodenNut.WNAE
{
    #region Resolved

    /// <summary>
    /// 1 個の CmpFloat について、継承 / 上書きを解決した最終的な設定と、指摘をまとめたもの。
    /// </summary>
    internal class ResolvedCmpFloat : IWNAEResolved
    {
        public CmpFloatEntry Entry;
        public CmpFloatRange Range;

        /// <summary>上書き設定または既存 VRCExpressionParameters から解決した Default。</summary>
        public float DefaultValue;

        /// <summary>上書き設定または既存 VRCExpressionParameters から解決した Saved。</summary>
        public bool Saved;

        /// <summary>継承元となる既存の Expression Parameter。無ければ null。</summary>
        public VRCExpressionParameters.Parameter Existing;

        public List<WNAEIssue> Issues { get; } = new List<WNAEIssue>();

        public string ParameterName => Entry != null ? Entry.name : "";
        public int BitCount => Range.IsValid ? Range.Bits : 0;
        public string BoolNameAt(int index) => Entry.BoolName(index);

        public bool IsNameCheckable =>
            Entry != null && !string.IsNullOrWhiteSpace(Entry.name) && Range.IsValid;
    }

    #endregion

    #region Expander

    internal static class CmpFloatExpander
    {
        /// <summary>これを超えるビット数は警告（生成 State 数が膨らむ）。</summary>
        public const int WarnBits = 6;

        public static string DescribeResult(ResolvedCmpFloat item)
        {
            return $"値域 {item.Range.Min}〜{item.Range.Max} を {item.Range.Bits} bit " +
                   $"({item.Range.Levels} 段階 / 分解能 {item.Range.Step:0.####}) に圧縮";
        }

        /// <summary>
        /// CmpFloat は値域とビット数を明示指定するため、CmpInt のような Animator 走査は行わない。
        /// </summary>
        public static List<ResolvedCmpFloat> Resolve(
            IEnumerable<WNAE_CmpFloatSettings> components, VRCAvatarDescriptor descriptor)
        {
            var parameters = descriptor != null ? descriptor.expressionParameters : null;

            return components
                .Where(c => c != null && c.entries != null)
                .SelectMany(c => c.entries)
                .Where(e => e != null)
                .Select(e => Resolve(e, parameters))
                .ToList();
        }

        public static ResolvedCmpFloat Resolve(CmpFloatEntry entry, VRCExpressionParameters parameters)
        {
            var resolved = new ResolvedCmpFloat { Entry = entry };

            resolved.Existing = parameters == null || parameters.parameters == null
                ? null
                : parameters.parameters.FirstOrDefault(p => p != null && p.name == entry.name);

            // Default / Saved は「上書き」か「既存から継承」かを先に確定させる
            resolved.DefaultValue = entry.overrideDefaultValue
                ? entry.defaultValue
                : resolved.Existing != null ? resolved.Existing.defaultValue : 0f;

            resolved.Saved = entry.overrideSaved
                ? entry.saved
                : resolved.Existing != null && resolved.Existing.saved;

            resolved.Range = new CmpFloatRange(entry.minValue, entry.maxValue, entry.bits);

            Validate(resolved);
            return resolved;
        }

        /// <summary>値域とビット数の検証。名前の重複・衝突は WNAENameValidator が担当する。</summary>
        public static void Validate(ResolvedCmpFloat item)
        {
            var entry = item.Entry;

            if (entry == null)
            {
                item.Issues.Add(new WNAEIssue(WNAEIssueLevel.Error, "エントリが空です。"));
                return;
            }

            if (string.IsNullOrWhiteSpace(entry.name))
            {
                item.Issues.Add(new WNAEIssue(WNAEIssueLevel.Error, "パラメータ名が空です。"));
                return;
            }

            if (!WNAEUtil.IsFinite(entry.minValue) || !WNAEUtil.IsFinite(entry.maxValue) ||
                !WNAEUtil.IsFinite(item.DefaultValue))
            {
                item.Issues.Add(new WNAEIssue(WNAEIssueLevel.Error,
                    "Range / Default に NaN や Infinity は指定できません。"));
                return;
            }

            if (!entry.overrideDefaultValue && item.Existing == null)
            {
                item.Issues.Add(new WNAEIssue(WNAEIssueLevel.Info,
                    "継承元の Expression Parameter が無いため、Default は 0 / Saved は OFF になります。"));
            }

            // VRChat の Float パラメータは -1〜1 しか表せない
            if (entry.minValue < -CmpFloatUtil.RangeLimit || entry.minValue > CmpFloatUtil.RangeLimit ||
                entry.maxValue < -CmpFloatUtil.RangeLimit || entry.maxValue > CmpFloatUtil.RangeLimit)
            {
                item.Issues.Add(new WNAEIssue(WNAEIssueLevel.Error,
                    $"Range は -{CmpFloatUtil.RangeLimit}〜{CmpFloatUtil.RangeLimit} の範囲で指定してください" +
                    $"（現在 {entry.minValue}〜{entry.maxValue}）。"));
            }

            if (entry.minValue >= entry.maxValue)
            {
                item.Issues.Add(new WNAEIssue(WNAEIssueLevel.Error,
                    $"Max ({entry.maxValue}) は Min ({entry.minValue}) より大きくしてください。"));
            }

            if (entry.bits < CmpFloatUtil.MinBits || entry.bits > CmpFloatUtil.MaxBits)
            {
                item.Issues.Add(new WNAEIssue(WNAEIssueLevel.Error,
                    $"Bits は {CmpFloatUtil.MinBits}〜{CmpFloatUtil.MaxBits} で指定してください（現在 {entry.bits}）。"));
            }

            if (!item.Range.IsValid) return;

            // 間隔が 0 でなくても、狭い Range では隣接段階や境界が同じ float に丸まる。
            // 復号した各段階が自分自身へ再エンコードされることまで確認する。
            if (item.Range.Step <= 0f || Enumerable.Range(0, item.Range.Levels)
                    .Any(i => item.Range.IndexOf(item.Range.ValueOf(i)) != i))
            {
                item.Issues.Add(new WNAEIssue(WNAEIssueLevel.Error,
                    "Range が狭すぎるため、指定した Bits で段階を表現できません。"));
                return;
            }

            if (entry.bits >= WarnBits)
            {
                item.Issues.Add(new WNAEIssue(WNAEIssueLevel.Warning,
                    $"{entry.bits} bit では {item.Range.Levels} 段階になり、エンコード/デコード合わせて約 " +
                    $"{item.Range.Levels * 2} State 生成されます。ビルド時間と Animator のサイズに影響します。"));
            }

            var clamped = item.Range.Clamp(item.DefaultValue);
            if (!Mathf.Approximately(clamped, item.DefaultValue))
            {
                item.Issues.Add(new WNAEIssue(WNAEIssueLevel.Warning,
                    $"Default ({item.DefaultValue}) が指定した値域外です。ビルド時に {clamped} へ丸められます。"));
            }
        }

        public static void ApplyExpressionParameters(
            WNAEExpressionParameters parameters, List<ResolvedCmpFloat> items)
        {
            foreach (var item in items)
            {
                var entry = item.Entry;

                // エンコード結果と食い違わないよう、Default も量子化後の値にそろえる
                var defaultIndex = item.Range.IndexOf(item.DefaultValue);
                var defaultValue = item.Range.ValueOf(defaultIndex);

                // CmpFloat 本体: Synced のチェック状態に関わらず非同期化する
                var floatParam = parameters.GetOrAdd(entry.name);
                floatParam.valueType = VRCExpressionParameters.ValueType.Float;
                floatParam.networkSynced = false;
                floatParam.saved = item.Saved;
                floatParam.defaultValue = defaultValue;

                for (var bit = 0; bit < item.Range.Bits; bit++)
                {
                    parameters.SetSyncedBool(entry.BoolName(bit), WNAEUtil.GetBit(defaultIndex, bit));
                }
            }
        }

        /// <summary>元の Float と生成 Bool 群をコントローラに宣言する。</summary>
        public static void DeclareParameters(
            VirtualAnimatorController controller, List<ResolvedCmpFloat> items)
        {
            foreach (var item in items)
            {
                var entry = item.Entry;
                var defaultIndex = item.Range.IndexOf(item.DefaultValue);

                controller.SetParameter(entry.name, new AnimatorControllerParameter
                {
                    name = entry.name,
                    type = AnimatorControllerParameterType.Float,
                    defaultFloat = item.Range.ValueOf(defaultIndex),
                });

                for (var bit = 0; bit < item.Range.Bits; bit++)
                {
                    WNAEAnimator.EnsureBoolParameter(
                        controller, entry.BoolName(bit), WNAEUtil.GetBit(defaultIndex, bit));
                }
            }
        }

        public static void BuildLayers(
            VirtualAnimatorController fx,
            List<ResolvedCmpFloat> items,
            VirtualClip emptyClip,
            bool writeDefaults)
        {
            foreach (var item in items)
            {
                BuildEncodeLayer(fx, item, emptyClip, writeDefaults);
                BuildDecodeLayer(fx, item, emptyClip, writeDefaults);
            }
        }

        /// <summary>
        /// ローカルで Float を量子化し、同期 Bool 群へ書き出すレイヤー。
        ///
        /// 下側 < value <= 上側という排他的な区間で振り分ける。
        /// Less(nextFloat(上側)) は、float で表現できる値に対して <= 上側と等価。
        /// 下限だけの優先順位判定では、自己遷移が除外された瞬間に下位段階へ落ちて発振する。
        /// </summary>
        private static void BuildEncodeLayer(
            VirtualAnimatorController controller,
            ResolvedCmpFloat item,
            VirtualClip emptyClip,
            bool writeDefaults)
        {
            var entry = item.Entry;
            var stateMachine = WNAEAnimator.CreateConversionLayer(
                controller, "Enc/" + entry.name, emptyClip, writeDefaults);

            var transitions = new List<VirtualStateTransition>();

            for (var index = item.Range.Levels - 1; index >= 0; index--)
            {
                var value = item.Range.ValueOf(index);

                var state = stateMachine.AddState(
                    $"={value:0.###}", emptyClip, new Vector3(320f, index * 55f, 0f));
                state.WriteDefaultValues = writeDefaults;

                // 同期 Bool への書き込みはローカルのみ
                var driver = WNAEAnimator.CreateDriver($"WNAE Enc {entry.name} = {value:0.###}", localOnly: true);
                for (var bit = 0; bit < item.Range.Bits; bit++)
                {
                    WNAEAnimator.AddSet(driver, entry.BoolName(bit), WNAEUtil.GetBit(index, bit) ? 1f : 0f);
                }

                state.Behaviours = state.Behaviours.Add(driver);

                var conditions = new List<AnimatorCondition>
                {
                    WNAEAnimator.Condition(AnimatorConditionMode.If, WNAEAnimator.IsLocalParameter, 0f),
                };

                // 最下段は下限を設けず、Range 未満の入力も受け止める
                if (index > 0)
                {
                    conditions.Add(WNAEAnimator.Condition(
                        AnimatorConditionMode.Greater, entry.name, item.Range.LowerThreshold(index)));
                }

                if (index < item.Range.Levels - 1)
                {
                    conditions.Add(WNAEAnimator.Condition(
                        AnimatorConditionMode.Less, entry.name,
                        WNAEUtil.NextFloatUp(item.Range.LowerThreshold(index + 1))));
                }

                transitions.Add(WNAEAnimator.CreateAnyStateTransition(state, conditions));
            }

            stateMachine.AnyStateTransitions = ImmutableList.CreateRange(transitions);
        }

        /// <summary>同期 Bool 群を読み、Float へ戻すレイヤー。</summary>
        private static void BuildDecodeLayer(
            VirtualAnimatorController controller,
            ResolvedCmpFloat item,
            VirtualClip emptyClip,
            bool writeDefaults)
        {
            var entry = item.Entry;
            var stateMachine = WNAEAnimator.CreateConversionLayer(
                controller, "Dec/" + entry.name, emptyClip, writeDefaults);

            var transitions = new List<VirtualStateTransition>();

            for (var index = 0; index < item.Range.Levels; index++)
            {
                var value = item.Range.ValueOf(index);

                var state = stateMachine.AddState(
                    $"={value:0.###}", emptyClip, new Vector3(320f, index * 55f, 0f));
                state.WriteDefaultValues = writeDefaults;

                // 書き込み先は非同期 Float なので、リモートでも実行させる
                var driver = WNAEAnimator.CreateDriver($"WNAE Dec {entry.name} = {value:0.###}", localOnly: false);
                WNAEAnimator.AddSet(driver, entry.name, value);
                state.Behaviours = state.Behaviours.Add(driver);

                // Bool -> Float はリモートでのみ行う（ローカルは連続値のまま使う）
                var conditions = new List<AnimatorCondition>
                {
                    WNAEAnimator.Condition(AnimatorConditionMode.IfNot, WNAEAnimator.IsLocalParameter, 0f),
                };

                for (var bit = 0; bit < item.Range.Bits; bit++)
                {
                    conditions.Add(WNAEAnimator.Condition(
                        WNAEUtil.GetBit(index, bit) ? AnimatorConditionMode.If : AnimatorConditionMode.IfNot,
                        entry.BoolName(bit),
                        0f));
                }

                transitions.Add(WNAEAnimator.CreateAnyStateTransition(state, conditions));
            }

            stateMachine.AnyStateTransitions = ImmutableList.CreateRange(transitions);
        }
    }

    #endregion

    #region Inspector

    [CustomEditor(typeof(WNAE_CmpFloatSettings))]
    internal class WNAE_CmpFloatSettingsEditor : WNAESettingsEditor
    {
        private List<ResolvedCmpFloat> _resolved = new List<ResolvedCmpFloat>();

        /// <summary>Options を開いたときの Default / Saved / Range / Bits / Bool Prefix。</summary>
        private const int OptionRows = 5;

        protected override string ListHeader => "CmpFloat パラメータ";
        protected override string ParameterKindLabel => "Float パラメータ";
        protected override AnimatorControllerParameterType AnimatorType => AnimatorControllerParameterType.Float;

        protected override VRCExpressionParameters.ValueType ExpressionType =>
            VRCExpressionParameters.ValueType.Float;

        protected override void RefreshResolved(VRCAvatarDescriptor descriptor)
        {
            var settings = (WNAE_CmpFloatSettings)target;
            var entries = settings.entries ?? new List<CmpFloatEntry>();
            var parameters = descriptor != null ? descriptor.expressionParameters : null;

            _resolved = entries
                .Select(e => e == null
                    ? new ResolvedCmpFloat { Entry = null }
                    : CmpFloatExpander.Resolve(e, parameters))
                .ToList();

            WNAENameValidator.Validate(
                _resolved.Cast<IWNAEResolved>().ToList(),
                WNAENameValidator.CollectExistingNames(descriptor));
        }

        private ResolvedCmpFloat ResolvedAt(int index)
        {
            return index >= 0 && index < _resolved.Count ? _resolved[index] : null;
        }

        protected override void OnAddEntry(SerializedProperty element)
        {
            element.FindPropertyRelative("name").stringValue = "";
            element.FindPropertyRelative("overrideDefaultValue").boolValue = false;
            element.FindPropertyRelative("defaultValue").floatValue = 0f;
            element.FindPropertyRelative("overrideSaved").boolValue = false;
            element.FindPropertyRelative("saved").boolValue = true;
            element.FindPropertyRelative("minValue").floatValue = 0f;
            element.FindPropertyRelative("maxValue").floatValue = 1f;
            element.FindPropertyRelative("bits").intValue = CmpFloatUtil.DefaultBits;
            element.FindPropertyRelative("overrideBoolPrefix").boolValue = false;
            element.FindPropertyRelative("boolPrefixOverride").stringValue = "";
        }

        protected override float ElementHeight(int index)
        {
            var rows = AlwaysVisibleRows;

            if (index >= 0 && index < Entries.arraySize && Entries.GetArrayElementAtIndex(index).isExpanded)
            {
                rows += OptionRows;
            }

            return rows * (Line + Pad) + Pad * 2 + ErrorsHeight(ResolvedAt(index));
        }

        protected override void DrawElement(Rect rect, int index, bool isActive, bool isFocused)
        {
            if (index < 0 || index >= Entries.arraySize) return;

            var element = Entries.GetArrayElementAtIndex(index);
            var resolved = ResolvedAt(index);

            var row = new Rect(rect.x, rect.y + Pad, rect.width, Line);

            Rect NextRow()
            {
                var r = row;
                row.y += Line + Pad;
                return r;
            }

            DrawNameField(NextRow(), element.FindPropertyRelative("name"));

            element.isExpanded = EditorGUI.Foldout(NextRow(), element.isExpanded, "Options", true);

            if (element.isExpanded) DrawOptions(element, resolved, NextRow);

            if (resolved != null) DrawErrors(resolved, ref row);
        }

        /// <summary>
        /// Range と Bits は常時有効なので上書きトグルを持たない。
        /// トグル分だけ字下げして、他の行と列を揃える。
        /// </summary>
        private static void DrawOptions(SerializedProperty element, ResolvedCmpFloat resolved, Func<Rect> nextRow)
        {
            var entry = resolved?.Entry;

            DrawOverrideRow(nextRow(), element.FindPropertyRelative("overrideDefaultValue"),
                new GUIContent("Default", "OFF のときは既存の Expression Parameter の Default をそのまま使います。"),
                resolved != null ? resolved.DefaultValue.ToString("0.###") : "-",
                (rect, label) => EditorGUI.PropertyField(
                    rect, element.FindPropertyRelative("defaultValue"), label));

            DrawOverrideRow(nextRow(), element.FindPropertyRelative("overrideSaved"),
                new GUIContent("Saved",
                    "Float の値をアバターに保存します。圧縮用の Bool は毎回 Animator が Set し直すため保存されません。\n" +
                    "OFF のときは既存の Expression Parameter の Saved をそのまま使います。"),
                resolved != null ? (resolved.Saved ? "ON" : "OFF") : "-",
                (rect, label) => EditorGUI.PropertyField(
                    rect, element.FindPropertyRelative("saved"), label));

            DrawPlainRow(nextRow(),
                new GUIContent("Range", "圧縮する値の範囲。VRChat の Float は -1〜1 しか表せません。"),
                (rect, label) =>
                {
                    var fieldRect = EditorGUI.PrefixLabel(rect, label);
                    DrawMinMaxFields(fieldRect,
                        element.FindPropertyRelative("minValue"),
                        element.FindPropertyRelative("maxValue"));
                });

            DrawPlainRow(nextRow(),
                new GUIContent("Bits",
                    $"圧縮後のビット数（{CmpFloatUtil.MinBits}〜{CmpFloatUtil.MaxBits}）。" +
                    "大きいほど精度が上がりますが、生成される State も増えます。"),
                (rect, label) => EditorGUI.PropertyField(rect, element.FindPropertyRelative("bits"), label));

            DrawOverrideRow(nextRow(), element.FindPropertyRelative("overrideBoolPrefix"),
                new GUIContent("Bool Prefix", "OFF のときは \"{Name}_b\" が使われます。"),
                entry != null ? CmpFloatUtil.BoolPrefix(entry) : "-",
                (rect, label) => EditorGUI.PropertyField(
                    rect, element.FindPropertyRelative("boolPrefixOverride"), label));
        }
    }

    #endregion
}
