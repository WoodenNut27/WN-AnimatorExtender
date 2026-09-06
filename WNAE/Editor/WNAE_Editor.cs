using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.IMGUI.Controls;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;
using VRC.SDKBase;
// UnityEditorInternal は AnimatorControllerParameter などを再定義しているため、必要な型だけ取り込む
using ReorderableList = UnityEditorInternal.ReorderableList;
using Object = UnityEngine.Object;

[assembly: ExportsPlugin(typeof(WoodenNut.WNAE.WNAEPlugin))]

namespace WoodenNut.WNAE
{
    #region Issues

    internal enum WNAEIssueLevel
    {
        Info,
        Warning,
        Error,
    }

    internal struct WNAEIssue
    {
        public WNAEIssueLevel Level;
        public string Message;

        public WNAEIssue(WNAEIssueLevel level, string message)
        {
            Level = level;
            Message = message;
        }
    }

    /// <summary>
    /// CmpInt / CmpFloat の解決結果に共通する部分。
    /// パラメータ名と生成 Bool 名の衝突検査を型をまたいで行うために使う。
    /// </summary>
    internal interface IWNAEResolved
    {
        string ParameterName { get; }
        int BitCount { get; }
        string BoolNameAt(int index);
        List<WNAEIssue> Issues { get; }

        /// <summary>名前も値域も正しく、名前検査の対象にできるか。</summary>
        bool IsNameCheckable { get; }
    }

    internal static class WNAEIssueUtil
    {
        public static bool HasError(this IWNAEResolved resolved)
        {
            return resolved.Issues.Any(i => i.Level == WNAEIssueLevel.Error);
        }

        /// <summary>Inspector にはエラーだけを出す。警告と情報はビルドログ側で報告する。</summary>
        public static IEnumerable<WNAEIssue> Errors(this IWNAEResolved resolved)
        {
            return resolved.Issues.Where(i => i.Level == WNAEIssueLevel.Error);
        }

        public static void LogAll(IWNAEResolved resolved, string kind, string label)
        {
            foreach (var issue in resolved.Issues)
            {
                var message = $"[WNAE] {kind} \"{label}\": {issue.Message}";
                switch (issue.Level)
                {
                    case WNAEIssueLevel.Error:
                        UnityEngine.Debug.LogError(message);
                        break;
                    case WNAEIssueLevel.Warning:
                        UnityEngine.Debug.LogWarning(message);
                        break;
                    default:
                        UnityEngine.Debug.Log(message);
                        break;
                }
            }
        }
    }

    /// <summary>パラメータ名と生成 Bool 名の重複 / 衝突を、CmpInt と CmpFloat をまたいで検査する。</summary>
    internal static class WNAENameValidator
    {
        public static void Validate(
            IReadOnlyList<IWNAEResolved> items, ICollection<string> existingParameterNames)
        {
            // 自分たちが所有する名前は「衝突」ではないので、既存名から差し引く
            var owned = new HashSet<string>();
            foreach (var item in items)
            {
                if (!item.IsNameCheckable) continue;

                owned.Add(item.ParameterName);
                for (var i = 0; i < item.BitCount; i++) owned.Add(item.BoolNameAt(i));
            }

            var foreign = new HashSet<string>(existingParameterNames ?? Array.Empty<string>());
            foreign.ExceptWith(owned);

            var seenNames = new Dictionary<string, int>();
            var seenBools = new Dictionary<string, int>();

            for (var i = 0; i < items.Count; i++)
            {
                var item = items[i];
                if (!item.IsNameCheckable) continue;

                if (seenNames.TryGetValue(item.ParameterName, out var prev))
                {
                    item.Issues.Add(new WNAEIssue(WNAEIssueLevel.Error,
                        $"パラメータ名 \"{item.ParameterName}\" が {prev + 1} 番目のエントリと重複しています。"));
                }
                else
                {
                    seenNames[item.ParameterName] = i;
                }

                for (var b = 0; b < item.BitCount; b++)
                {
                    var boolName = item.BoolNameAt(b);

                    if (foreign.Contains(boolName))
                    {
                        item.Issues.Add(new WNAEIssue(WNAEIssueLevel.Error,
                            $"生成される Bool 名 \"{boolName}\" が既存のパラメータと衝突しています。" +
                            "Bool Prefix を設定してください。"));
                    }

                    if (seenBools.TryGetValue(boolName, out var prevBool) && prevBool != i)
                    {
                        item.Issues.Add(new WNAEIssue(WNAEIssueLevel.Error,
                            $"生成される Bool 名 \"{boolName}\" が {prevBool + 1} 番目のエントリと衝突しています。"));
                    }
                    else
                    {
                        seenBools[boolName] = i;
                    }
                }
            }
        }

        public static HashSet<string> CollectExistingNames(VRCAvatarDescriptor descriptor)
        {
            var existing = new HashSet<string>();
            if (descriptor == null || descriptor.expressionParameters == null ||
                descriptor.expressionParameters.parameters == null)
            {
                return existing;
            }

            foreach (var p in descriptor.expressionParameters.parameters)
            {
                if (p != null && !string.IsNullOrEmpty(p.name)) existing.Add(p.name);
            }

            return existing;
        }
    }

    #endregion

    #region Parameter catalog

    /// <summary>
    /// アバターに登録されているパラメータの一覧。
    /// NDMF の introspection API を使うので、VRCExpressionParameters だけでなく
    /// MA Parameters / MA Menu Item が供給するパラメータ（リネーム後の名前）も拾える。
    /// </summary>
    internal static class WNAEParameterCatalog
    {
        internal struct Candidate
        {
            public string Name;
            public string Source;
        }

        public static List<Candidate> Collect(
            VRCAvatarDescriptor descriptor,
            AnimatorControllerParameterType animatorType,
            VRCExpressionParameters.ValueType expressionType)
        {
            var result = new List<Candidate>();
            if (descriptor == null) return result;

            var seen = new HashSet<string>();

            try
            {
                foreach (var provided in ParameterInfo.ForUI.GetParametersForObject(descriptor.gameObject))
                {
                    if (provided.Namespace != ParameterNamespace.Animator) continue;
                    if (provided.ParameterType != animatorType) continue;
                    if (provided.IsHidden) continue;

                    var name = provided.EffectiveName;
                    if (string.IsNullOrEmpty(name) || !seen.Add(name)) continue;

                    result.Add(new Candidate { Name = name, Source = DescribeSource(provided.Source) });
                }
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogWarning($"[WNAE] NDMF のパラメータ一覧の取得に失敗しました: {e.Message}");
            }

            // introspection が使えない場合の保険として、Expression Parameters を直接読む
            var parameters = descriptor.expressionParameters;
            if (parameters != null && parameters.parameters != null)
            {
                foreach (var p in parameters.parameters)
                {
                    if (p == null || string.IsNullOrEmpty(p.name)) continue;
                    if (p.valueType != expressionType) continue;
                    if (!seen.Add(p.name)) continue;

                    result.Add(new Candidate { Name = p.name, Source = "Expression Parameters" });
                }
            }

            result.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            return result;
        }

        private static string DescribeSource(Component source)
        {
            if (source == null) return "";
            if (source is VRCAvatarDescriptor) return "Expression Parameters";

            var typeName = source.GetType().Name;
            if (typeName.StartsWith("ModularAvatar")) typeName = "MA " + typeName.Substring("ModularAvatar".Length);

            return $"{typeName} ({source.gameObject.name})";
        }
    }

    /// <summary>Name 欄のコンボボックス用ドロップダウン。検索できて、"/" を階層区切りとして扱わない。</summary>
    internal class WNAEParameterDropdown : AdvancedDropdown
    {
        private readonly List<WNAEParameterCatalog.Candidate> _candidates;
        private readonly Action<string> _onSelected;
        private readonly string _title;
        private readonly List<string> _names = new List<string>();

        public WNAEParameterDropdown(
            AdvancedDropdownState state,
            string title,
            List<WNAEParameterCatalog.Candidate> candidates,
            Action<string> onSelected) : base(state)
        {
            _title = title;
            _candidates = candidates;
            _onSelected = onSelected;
            minimumSize = new Vector2(260f, 320f);
        }

        protected override AdvancedDropdownItem BuildRoot()
        {
            var root = new AdvancedDropdownItem(_title);
            _names.Clear();

            if (_candidates == null || _candidates.Count == 0)
            {
                root.AddChild(new AdvancedDropdownItem($"{_title}が見つかりません") { enabled = false });
                return root;
            }

            foreach (var candidate in _candidates)
            {
                var label = string.IsNullOrEmpty(candidate.Source)
                    ? candidate.Name
                    : $"{candidate.Name}    －  {candidate.Source}";

                root.AddChild(new AdvancedDropdownItem(label) { id = _names.Count });
                _names.Add(candidate.Name);
            }

            return root;
        }

        protected override void ItemSelected(AdvancedDropdownItem item)
        {
            if (item.id >= 0 && item.id < _names.Count) _onSelected(_names[item.id]);
        }
    }

    #endregion

    #region Expression parameters

    /// <summary>
    /// ビルド中に書き換える VRCExpressionParameters。
    /// CmpInt と CmpFloat で 1 つのクローンを共有するため、クローンはここで 1 回だけ作る。
    /// </summary>
    internal class WNAEExpressionParameters
    {
        public VRCExpressionParameters Asset { get; private set; }

        private readonly List<VRCExpressionParameters.Parameter> _list;

        private WNAEExpressionParameters(VRCExpressionParameters asset)
        {
            Asset = asset;
            _list = new List<VRCExpressionParameters.Parameter>(
                asset.parameters ?? Array.Empty<VRCExpressionParameters.Parameter>());
        }

        /// <summary>元アセットを壊さないよう複製してから、アバターに差し替える。</summary>
        public static WNAEExpressionParameters CloneFrom(BuildContext ctx, VRCAvatarDescriptor descriptor)
        {
            var source = descriptor.expressionParameters;
            VRCExpressionParameters cloned;

            if (source != null)
            {
                cloned = Object.Instantiate(source);
                cloned.name = source.name + " (WNAE)";
            }
            else
            {
                cloned = ScriptableObject.CreateInstance<VRCExpressionParameters>();
                cloned.name = "ExpressionParameters (WNAE)";
                cloned.parameters = Array.Empty<VRCExpressionParameters.Parameter>();
            }

            ctx.AssetSaver.SaveAsset(cloned);
            descriptor.expressionParameters = cloned;

            return new WNAEExpressionParameters(cloned);
        }

        public VRCExpressionParameters.Parameter GetOrAdd(string name)
        {
            var found = _list.FirstOrDefault(p => p != null && p.name == name);
            if (found != null) return found;

            found = new VRCExpressionParameters.Parameter { name = name };
            _list.Add(found);
            return found;
        }

        /// <summary>圧縮結果を書き出す同期 Bool を 1 本登録する。</summary>
        public void SetSyncedBool(string name, bool defaultOn)
        {
            var parameter = GetOrAdd(name);
            parameter.valueType = VRCExpressionParameters.ValueType.Bool;
            parameter.networkSynced = true;
            // 毎回 Animator が Set し直すため保存不要
            parameter.saved = false;
            parameter.defaultValue = defaultOn ? 1f : 0f;
        }

        public void Commit()
        {
            Asset.parameters = _list.ToArray();
        }

        public void ReportCost(int expandedCount)
        {
            var cost = Asset.CalcTotalCost();

            if (cost > VRCExpressionParameters.MAX_PARAMETER_COST)
            {
                UnityEngine.Debug.LogError(
                    $"[WNAE] 展開後の同期パラメータが {cost} bit で、上限 " +
                    $"{VRCExpressionParameters.MAX_PARAMETER_COST} bit を超えています。");
            }
            else
            {
                UnityEngine.Debug.Log(
                    $"[WNAE] {expandedCount} 件を展開しました。同期パラメータ: " +
                    $"{cost}/{VRCExpressionParameters.MAX_PARAMETER_COST} bit");
            }
        }
    }

    #endregion

    #region Animator helpers

    /// <summary>CmpInt / CmpFloat の変換レイヤー生成で共通に使う Animator 操作。</summary>
    internal static class WNAEAnimator
    {
        public const string LayerNamePrefix = "WNAE/";
        public const string IsLocalParameter = "IsLocal";

        public static VirtualAnimatorController GetOrCreateFxController(VirtualControllerContext controllerContext)
        {
            var key = VRCAvatarDescriptor.AnimLayerType.FX;

            if (controllerContext.Controllers.TryGetValue(key, out var fx) && fx != null) return fx;

            fx = VirtualAnimatorController.Create(controllerContext.CloneContext, "FX");
            controllerContext.Controllers[key] = fx;
            return fx;
        }

        /// <summary>既存 State の Write Defaults の多数派を返す（混在による VRChat の警告を避けるため）。</summary>
        public static bool DetermineWriteDefaults(VirtualAnimatorController controller)
        {
            var on = 0;
            var off = 0;

            foreach (var layer in controller.Layers)
            {
                var stateMachine = layer.StateMachine;
                if (stateMachine == null) continue;

                foreach (var state in stateMachine.AllStates())
                {
                    if (state.WriteDefaultValues) on++;
                    else off++;
                }
            }

            return off > on ? false : true;
        }

        public static void EnsureBoolParameter(
            VirtualAnimatorController controller, string name, bool defaultValue, bool keepExisting = false)
        {
            if (keepExisting && controller.Parameters.ContainsKey(name)) return;

            controller.SetParameter(name, new AnimatorControllerParameter
            {
                name = name,
                type = AnimatorControllerParameterType.Bool,
                defaultBool = defaultValue,
            });
        }

        /// <summary>Wait を既定 State に持つ、変換用の空レイヤーを作る。</summary>
        public static VirtualStateMachine CreateConversionLayer(
            VirtualAnimatorController controller,
            string layerName,
            VirtualClip emptyClip,
            bool writeDefaults)
        {
            var layer = controller.AddLayer(new LayerPriority(0), LayerNamePrefix + layerName);
            layer.DefaultWeight = 1f;

            var stateMachine = layer.StateMachine;
            stateMachine.EntryPosition = new Vector3(0f, -120f, 0f);
            stateMachine.AnyStatePosition = new Vector3(0f, -60f, 0f);
            stateMachine.ExitPosition = new Vector3(320f, -120f, 0f);

            var wait = stateMachine.AddState("Wait", emptyClip, Vector3.zero);
            wait.WriteDefaultValues = writeDefaults;
            stateMachine.DefaultState = wait;

            return stateMachine;
        }

        public static VirtualStateTransition CreateAnyStateTransition(
            VirtualState destination, IEnumerable<AnimatorCondition> conditions)
        {
            var transition = VirtualStateTransition.Create();
            transition.SetDestination(destination);
            transition.Conditions = ImmutableList.CreateRange(conditions);
            transition.ExitTime = null;
            transition.Duration = 0f;
            transition.HasFixedDuration = true;
            // Driver は State 進入時にのみ走る。自己遷移を許すと毎フレーム再発火してしまう
            transition.CanTransitionToSelf = false;
            return transition;
        }

        public static AnimatorCondition Condition(AnimatorConditionMode mode, string parameter, float threshold)
        {
            return new AnimatorCondition
            {
                mode = mode,
                parameter = parameter,
                threshold = threshold,
            };
        }

        public static VRCAvatarParameterDriver CreateDriver(string name, bool localOnly)
        {
            var driver = ScriptableObject.CreateInstance<VRCAvatarParameterDriver>();
            driver.name = name;
            driver.localOnly = localOnly;
            driver.parameters = new List<VRC_AvatarParameterDriver.Parameter>();
            return driver;
        }

        public static void AddSet(VRCAvatarParameterDriver driver, string parameter, float value)
        {
            driver.parameters.Add(new VRC_AvatarParameterDriver.Parameter
            {
                name = parameter,
                type = VRC_AvatarParameterDriver.ChangeType.Set,
                value = value,
            });
        }

        /// <summary>定数加算。Parameter Driver の Add はパラメータ同士の加算をサポートしない。</summary>
        public static void AddAdd(VRCAvatarParameterDriver driver, string parameter, float value)
        {
            driver.parameters.Add(new VRC_AvatarParameterDriver.Parameter
            {
                name = parameter,
                type = VRC_AvatarParameterDriver.ChangeType.Add,
                value = value,
            });
        }

        /// <summary>そのままの値のコピー。</summary>
        public static void AddCopy(VRCAvatarParameterDriver driver, string destination, string source)
        {
            driver.parameters.Add(new VRC_AvatarParameterDriver.Parameter
            {
                name = destination,
                source = source,
                type = VRC_AvatarParameterDriver.ChangeType.Copy,
                convertRange = false,
            });
        }

        /// <summary>
        /// Convert Range 付きのコピー。<c>dest = destMin + (src-srcMin)×(destMax-destMin)/(srcMax-srcMin)</c>
        /// という定数係数のアフィン変換になる。
        /// Int 宛ての丸め規則が SDK から確認できないため、結果が厳密な整数になる用途にのみ使うこと。
        /// </summary>
        public static void AddCopyRange(
            VRCAvatarParameterDriver driver, string destination, string source,
            float sourceMin, float sourceMax, float destMin, float destMax)
        {
            driver.parameters.Add(new VRC_AvatarParameterDriver.Parameter
            {
                name = destination,
                source = source,
                type = VRC_AvatarParameterDriver.ChangeType.Copy,
                convertRange = true,
                sourceMin = sourceMin,
                sourceMax = sourceMax,
                destMin = destMin,
                destMax = destMax,
            });
        }

        /// <summary>State から State への遷移。</summary>
        public static VirtualStateTransition CreateTransition(
            VirtualState destination, IEnumerable<AnimatorCondition> conditions)
        {
            var list = conditions == null
                ? ImmutableList<AnimatorCondition>.Empty
                : ImmutableList.CreateRange(conditions);

            var transition = VirtualStateTransition.Create();
            transition.SetDestination(destination);
            transition.Conditions = list;

            // 条件も Exit Time も無い遷移は Unity では成立しない。
            // 無条件に進みたい場合は Exit Time 0 を入れる必要がある。
            transition.ExitTime = list.IsEmpty ? 0f : (float?)null;
            transition.Duration = 0f;
            transition.HasFixedDuration = true;
            return transition;
        }

        public static void EnsureIntParameter(
            VirtualAnimatorController controller, string name, int defaultValue = 0)
        {
            if (controller.Parameters.ContainsKey(name)) return;

            controller.SetParameter(name, new AnimatorControllerParameter
            {
                name = name,
                type = AnimatorControllerParameterType.Int,
                defaultInt = defaultValue,
            });
        }
    }

    #endregion

    #region NDMF plugin

    /// <summary>
    /// CmpInt / CmpFloat をビルド時に同期 Bool 群へ展開する NDMF プラグイン。
    /// </summary>
    public class WNAEPlugin : Plugin<WNAEPlugin>
    {
        public override string QualifiedName => "wooden-nut.wnae";
        public override string DisplayName => "WNAE";

        protected override void Configure()
        {
            // Modular Avatar のパラメータリネームとメニュー結合が済んだ後の最終形を走査する必要がある
            InPhase(BuildPhase.Transforming)
                .AfterPlugin("nadena.dev.modular-avatar")
                .WithRequiredExtension(typeof(AnimatorServicesContext),
                    seq => seq.Run("Expand WNAE parameters", WNAEPass.Execute));
        }
    }

    /// <summary>
    /// CmpInt と CmpFloat をまとめて処理する単一のパス。
    /// VRCExpressionParameters のクローンとビット予算の検査を 1 回で済ませるため、
    /// 型ごとにパスを分けずここで束ねている。
    /// </summary>
    internal static class WNAEPass
    {
        internal static void Execute(BuildContext ctx)
        {
            var controllerContext = ctx.Extension<AnimatorServicesContext>().ControllerContext;

            // Behaviour の展開は CmpInt の値域検出より先に行う。
            // 展開後の "Copy acc -> c" をスキャナが「Convert Range 未使用の Copy」として検出し、
            // 演算結果を CmpInt にする場合は Range Override が必要だと警告できる。
            WNAEBehaviourExpander.Expand(controllerContext);

            var intSettings = ctx.AvatarRootObject.GetComponentsInChildren<WNAE_CmpIntSettings>(true);
            var floatSettings = ctx.AvatarRootObject.GetComponentsInChildren<WNAE_CmpFloatSettings>(true);

            if (intSettings.Length == 0 && floatSettings.Length == 0) return;

            try
            {
                var descriptor = ctx.AvatarRootObject.GetComponent<VRCAvatarDescriptor>();
                if (descriptor == null)
                {
                    UnityEngine.Debug.LogError("[WNAE] VRCAvatarDescriptor が見つからないため展開をスキップしました。");
                    return;
                }

                // 自分のレイヤーを足す前のコントローラを走査する（CmpInt の値域検出に必要）
                var controllers = controllerContext.Controllers.Values.ToList();

                var ints = CmpIntExpander.Resolve(intSettings, controllers, descriptor);
                var floats = CmpFloatExpander.Resolve(floatSettings, descriptor);

                // 名前の衝突は CmpInt と CmpFloat をまたいで起こり得るのでまとめて検査する
                var all = ints.Cast<IWNAEResolved>().Concat(floats).ToList();
                WNAENameValidator.Validate(all, WNAENameValidator.CollectExistingNames(descriptor));

                var acceptedInts = Accept(ints, "CmpInt", CmpIntExpander.DescribeResult);
                var acceptedFloats = Accept(floats, "CmpFloat", CmpFloatExpander.DescribeResult);

                if (acceptedInts.Count == 0 && acceptedFloats.Count == 0) return;

                var parameters = WNAEExpressionParameters.CloneFrom(ctx, descriptor);
                CmpIntExpander.ApplyExpressionParameters(parameters, acceptedInts);
                CmpFloatExpander.ApplyExpressionParameters(parameters, acceptedFloats);
                parameters.Commit();
                parameters.ReportCost(acceptedInts.Count + acceptedFloats.Count);

                BuildAnimatorLayers(controllerContext, acceptedInts, acceptedFloats);
            }
            finally
            {
                foreach (var component in intSettings)
                {
                    if (component != null) Object.DestroyImmediate(component);
                }

                foreach (var component in floatSettings)
                {
                    if (component != null) Object.DestroyImmediate(component);
                }
            }
        }

        /// <summary>検証結果をログに出し、エラーのないものだけ返す。</summary>
        private static List<T> Accept<T>(List<T> items, string kind, Func<T, string> describe)
            where T : IWNAEResolved
        {
            var accepted = new List<T>();

            foreach (var item in items)
            {
                var label = string.IsNullOrWhiteSpace(item.ParameterName) ? "(名前なし)" : item.ParameterName;

                WNAEIssueUtil.LogAll(item, kind, label);

                if (item.HasError())
                {
                    UnityEngine.Debug.LogError($"[WNAE] {kind} \"{label}\" はエラーのため展開をスキップしました。");
                    continue;
                }

                UnityEngine.Debug.Log($"[WNAE] {kind} \"{label}\": {describe(item)}");
                accepted.Add(item);
            }

            return accepted;
        }

        private static void BuildAnimatorLayers(
            VirtualControllerContext controllerContext,
            List<ResolvedCmpInt> ints,
            List<ResolvedCmpFloat> floats)
        {
            var fx = WNAEAnimator.GetOrCreateFxController(controllerContext);

            // 元パラメータと生成 Bool は全コントローラに宣言しておき、
            // FX で圧縮した結果の Bool を他のレイヤーからも同じ名前で参照できるようにする
            foreach (var controller in controllerContext.Controllers.Values.ToList())
            {
                if (controller == null) continue;

                CmpIntExpander.DeclareParameters(controller, ints);
                CmpFloatExpander.DeclareParameters(controller, floats);
            }

            // 変換レイヤーは FX にだけ置く
            WNAEAnimator.EnsureBoolParameter(fx, WNAEAnimator.IsLocalParameter, false, keepExisting: true);

            // 我々のレイヤーを足す前に、既存 State の Write Defaults の多数派を調べておく
            var writeDefaults = WNAEAnimator.DetermineWriteDefaults(fx);
            var emptyClip = VirtualClip.Create("WNAE Empty");

            CmpIntExpander.BuildLayers(fx, ints, emptyClip, writeDefaults);
            CmpFloatExpander.BuildLayers(fx, floats, emptyClip, writeDefaults);
        }
    }

    #endregion

    #region Inspector base

    /// <summary>
    /// CmpInt / CmpFloat の Inspector で共通するレイアウト部品と、走査結果のキャッシュ機構。
    /// </summary>
    internal abstract class WNAESettingsEditor : UnityEditor.Editor
    {
        protected static readonly float Line = EditorGUIUtility.singleLineHeight;
        protected static readonly float Pad = EditorGUIUtility.standardVerticalSpacing;

        /// <summary>上書きトグルの幅。トグルの無い行もこの分だけ字下げして列を揃える。</summary>
        protected const float ToggleWidth = 18f;

        /// <summary>Name 行と Options の Foldout 行。</summary>
        protected const int AlwaysVisibleRows = 2;

        protected ReorderableList List;
        protected SerializedProperty Entries;

        protected List<WNAEParameterCatalog.Candidate> Candidates = new List<WNAEParameterCatalog.Candidate>();

        private bool _needsRefresh = true;
        private readonly AdvancedDropdownState _dropdownState = new AdvancedDropdownState();

        protected abstract string ListHeader { get; }
        protected abstract string ParameterKindLabel { get; }
        protected abstract AnimatorControllerParameterType AnimatorType { get; }
        protected abstract VRCExpressionParameters.ValueType ExpressionType { get; }

        protected abstract void RefreshResolved(VRCAvatarDescriptor descriptor);
        protected abstract float ElementHeight(int index);
        protected abstract void DrawElement(Rect rect, int index, bool isActive, bool isFocused);
        protected abstract void OnAddEntry(SerializedProperty element);

        protected virtual void OnEnable()
        {
            Entries = serializedObject.FindProperty("entries");

            List = new ReorderableList(serializedObject, Entries, true, true, true, true)
            {
                drawHeaderCallback = rect => EditorGUI.LabelField(rect, ListHeader),
                elementHeightCallback = ElementHeight,
                drawElementCallback = DrawElement,
                onAddCallback = OnAdd,
            };

            _needsRefresh = true;
            Undo.undoRedoPerformed += MarkDirty;
        }

        protected virtual void OnDisable()
        {
            Undo.undoRedoPerformed -= MarkDirty;
        }

        protected void MarkDirty() => _needsRefresh = true;

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            if (_needsRefresh)
            {
                Refresh();
                _needsRefresh = false;
            }

            List.DoLayoutList();

            EditorGUILayout.Space();

            // Animator やメニューを別ウィンドウで編集した場合は、このボタンで検出をやり直す
            if (GUILayout.Button("再スキャン")) MarkDirty();

            if (serializedObject.ApplyModifiedProperties()) MarkDirty();
        }

        private void Refresh()
        {
            var descriptor = ((Component)target).GetComponentInParent<VRCAvatarDescriptor>();

            Candidates = WNAEParameterCatalog.Collect(descriptor, AnimatorType, ExpressionType);
            RefreshResolved(descriptor);
        }

        private void OnAdd(ReorderableList list)
        {
            var index = Entries.arraySize;
            Entries.arraySize++;

            var element = Entries.GetArrayElementAtIndex(index);
            OnAddEntry(element);

            // Options は既定で閉じておく
            element.isExpanded = false;

            list.index = index;
        }

        protected static float HelpBoxHeight(string message)
        {
            var width = Mathf.Max(100f, EditorGUIUtility.currentViewWidth - 90f);
            return Mathf.Max(Line * 2f, EditorStyles.helpBox.CalcHeight(new GUIContent(message), width));
        }

        /// <summary>エラーの HelpBox を描き、消費した高さだけ row を進める。</summary>
        protected static void DrawErrors(IWNAEResolved resolved, ref Rect row)
        {
            foreach (var issue in resolved.Errors())
            {
                var height = HelpBoxHeight(issue.Message);
                EditorGUI.HelpBox(new Rect(row.x, row.y, row.width, height), issue.Message, MessageType.Error);
                row.y += height + Pad;
            }
        }

        protected static float ErrorsHeight(IWNAEResolved resolved)
        {
            if (resolved == null) return 0f;

            var height = 0f;
            foreach (var issue in resolved.Errors()) height += HelpBoxHeight(issue.Message) + Pad;
            return height;
        }

        /// <summary>左端に上書きトグル、右にフィールド（OFF なら継承値の表示）を描く。</summary>
        protected static void DrawOverrideRow(
            Rect rect,
            SerializedProperty overrideProp,
            GUIContent label,
            string inheritedText,
            Action<Rect, GUIContent> drawValue)
        {
            var toggleRect = new Rect(rect.x, rect.y, ToggleWidth, rect.height);
            overrideProp.boolValue = EditorGUI.Toggle(toggleRect, overrideProp.boolValue);

            var contentRect = Indent(rect);

            var labelWidth = EditorGUIUtility.labelWidth;
            EditorGUIUtility.labelWidth = labelWidth - ToggleWidth;

            if (overrideProp.boolValue)
            {
                drawValue(contentRect, label);
            }
            else
            {
                using (new EditorGUI.DisabledScope(true))
                {
                    // 第 2 引数を string にすると GUIStyle への暗黙変換が選ばれ、
                    // スタイル名が見つからずエラースタイル（赤字）になるため GUIContent で渡す
                    EditorGUI.LabelField(contentRect, label, new GUIContent($"継承: {inheritedText}"));
                }
            }

            EditorGUIUtility.labelWidth = labelWidth;
        }

        /// <summary>上書きトグルを持たない項目の行。トグル分だけ字下げして列を揃える。</summary>
        protected static void DrawPlainRow(Rect rect, GUIContent label, Action<Rect, GUIContent> drawValue)
        {
            var labelWidth = EditorGUIUtility.labelWidth;
            EditorGUIUtility.labelWidth = labelWidth - ToggleWidth;

            drawValue(Indent(rect), label);

            EditorGUIUtility.labelWidth = labelWidth;
        }

        /// <summary>トグル列の分だけ右に寄せた矩形。</summary>
        protected static Rect Indent(Rect rect)
        {
            return new Rect(rect.x + ToggleWidth, rect.y, rect.width - ToggleWidth, rect.height);
        }

        /// <summary>Min / Max を 1 行に並べる。</summary>
        protected static void DrawMinMaxFields(
            Rect contentRect, SerializedProperty minProp, SerializedProperty maxProp)
        {
            var half = contentRect.width * 0.5f;
            var minRect = new Rect(contentRect.x, contentRect.y, half - 4f, contentRect.height);
            var maxRect = new Rect(contentRect.x + half + 4f, contentRect.y, half - 4f, contentRect.height);

            var labelWidth = EditorGUIUtility.labelWidth;
            EditorGUIUtility.labelWidth = 32f;
            EditorGUI.PropertyField(minRect, minProp, new GUIContent("Min"));
            EditorGUI.PropertyField(maxRect, maxProp, new GUIContent("Max"));
            EditorGUIUtility.labelWidth = labelWidth;
        }

        /// <summary>
        /// 既存パラメータの圧縮のみを扱い新規追加はしないため、Name は選択専用のドロップダウンにする。
        /// AdvancedDropdown を使うのは検索が付くのと、GenericMenu と違って "/" を階層区切りとして
        /// 扱わないため（VRChat のパラメータ名には "/" がよく含まれる）。
        /// </summary>
        protected void DrawNameField(Rect rect, SerializedProperty nameProp)
        {
            var fieldRect = EditorGUI.PrefixLabel(rect, new GUIContent("Name"));

            var current = nameProp.stringValue;
            var content = new GUIContent(
                string.IsNullOrEmpty(current) ? $"（{ParameterKindLabel}を選択）" : current,
                $"アバターに登録されている{ParameterKindLabel}から選びます。");

            if (!EditorGUI.DropdownButton(fieldRect, content, FocusType.Keyboard, EditorStyles.popup)) return;

            // ドロップダウンのコールバックは後で走るため、パスから引き直して安全に書き込む
            var propertyPath = nameProp.propertyPath;
            var so = serializedObject;

            var dropdown = new WNAEParameterDropdown(_dropdownState, ParameterKindLabel, Candidates, selected =>
            {
                so.Update();
                var property = so.FindProperty(propertyPath);
                if (property == null) return;

                property.stringValue = selected;
                so.ApplyModifiedProperties();
                MarkDirty();
            });

            dropdown.Show(fieldRect);
        }
    }

    #endregion
}
