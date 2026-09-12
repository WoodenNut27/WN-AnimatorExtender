# WNAE - WN-AnimatorExtender

```
○名称　　　　：WN-AnimatorExtender
○バージョン　：β1
○公開日　　　：2026/08/02
○更新日　　　：2026/09/13
○作成者　　　：木製ナット
○連絡先　　　：Twitter @WoodenNut27
○ライセンス　：NYSL
○URL 　　　　：https://www.woodennut.com/

　本モジュールはNYSLに準拠します。
　NYSL 全文: http://www.kmonos.net/nysl/

　本モジュールを用いたいかなる損害・損失に対して、
　作成者は一切責任を負わないものとします。

　Copyright (C) 2026 WoodenNut
```

※本モジュールは VRChat Inc. とは無関係の非公式ツールです。

VRChat アバター向けの Unity 拡張です。同期パラメータの自動圧縮と、Animator 内での整数演算・ビット列変換を提供します。
4 つの機能はいずれも NDMF を使ってビルド時に自動展開されます。

---

## 目次

### 使う

| 節 | 内容 |
|---|---|
| [動作環境とインストール](#動作環境とインストール) | 依存パッケージと導入方法 |
| [1. WNAE CmpInt Settings](#1-wnae-cmpint-settings) | **Int** を、実際に使われている値域から必要最小限のビット数へ圧縮する |
| [2. WNAE CmpFloat Settings](#2-wnae-cmpfloat-settings) | **Float** を、指定した値域と精度（ビット数）へ量子化して圧縮する |
| [3. WNAE Parameter Calculation](#3-wnae-parameter-calculation) | Animator 内で **Int 同士の四則演算・ビット演算**を行う |
| [4. WNAE Parameter Encoder / Decoder](#4-wnae-parameter-encoder--decoder) | **8 個の Bool と 1 個の Int** を相互変換する |
| [他ツールとの関係](#他ツールとの関係) | Modular Avatar / Avatar Optimizer / メニュー / OSC との関係 |
| [エラーと警告](#エラーと警告) | エラーの一覧と、どこに出るか |
| [既知の制限・未検証事項](#既知の制限未検証事項) | 現時点で確認できていないこと |
| [更新履歴](#更新履歴) | 版ごとの変更点と、α1 から更新する際の注意 |

各機能の節は「概要 → 導入 → Inspector」を基本の順として並んでいます。**使うだけなら各節の前半（Inspector まで）だけ読めば足ります。**

### 仕組みを知る

使うだけなら読み飛ばして構いません。

| 節 | 内容 |
|---|---|
| [ビルド時の共通処理](#ビルド時の共通処理) | 4 機能に共通する展開の流れと、生成物の仕様 |
| [ファイル構成](#ファイル構成) | ソースファイルの役割 |

各機能の節にある「ビルド時の処理 / ビルド時の展開」も同じく実装寄りの内容です。

---

## 動作環境とインストール

| 依存 | 動作確認バージョン | 要否 |
|---|---|---|
| Unity | 2022.3 | 必須 |
| VRChat SDK - Avatars | 3.10.4 | 必須 |
| NDMF (`nadena.dev.ndmf`) | 1.14.1 | 必須 |
| Modular Avatar | 1.17.1 | 任意（[共存します](#他ツールとの関係)） |

`WNAE_xxx.unitypackage` をUnityプロジェクトにインポートしてください。（xxxはバージョン）

`WoodenNut/WNAE` を削除することでUnityプロジェクトからアンインストールされます。

---

## 1. WNAE CmpInt Settings

### 概要

VRChat の同期パラメータ予算は 256 bit で、`Int` は値域に関わらず一律 8 bit を消費します。実際には数個の選択肢しか持たない衣装・髪型スイッチが大半で、大量のビットが無駄になっています。

**CmpInt（可変長圧縮 Int）** は、この Int をビルド時に必要最小限のビット数の同期 `Bool` へ自動分解する仕組みです。値域 0〜5 なら 8 bit → 3 bit となり 5 bit 節約できます。

Animator 上では**普通の Int パラメータのまま**扱われるため、既存の遷移条件やアニメーションの作り方を一切変える必要がありません。

### 導入

アバタールートに **Add Component → WoodenNut → WNAE CmpInt Settings** を追加します。

1 つの GameObject に付けられるのは 1 個までですが、**アバター配下であればどの GameObject に置いても構いません**。複数の GameObject に分けて置いた場合、すべてのエントリがまとめて処理されます（衣装プレハブごとに設定を持たせる、といった使い方ができます）。

ビルド時に自動的に取り除かれるため（`IEditorOnly`）、アップロード後のアバターには残りません。

### Inspector

各エントリは `Name` のみが常に表示され、残りは `Options` の Foldout に格納されています。

```
Name      [ Costume                ▼ ]
▶ Options
```

#### Name

対象となる Int パラメータを、**アバターに登録済みの Int パラメータの一覧から選択**します（検索可）。WNAE は既存パラメータの圧縮だけを行い新規パラメータの追加はしないため、自由入力はできません。

一覧は NDMF の introspection API から取得しており、以下が含まれます。

- **VRCExpressionParameters** に登録された Int
- **MA Parameters** / **MA Menu Item** が供給する Int

MA によるリネームが適用された後の**最終的な名前**が表示されるため、そのまま選べば整合します。

#### Options

各項目の左端にチェックボックスがあり、**OFF なら既存設定を継承、ON なら上書き**します。OFF のときは実際に採用される値がグレーで表示されます。

| 項目 | OFF（継承） | ON（上書き） |
|---|---|---|
| **Default** | 既存 VRCExpressionParameters の `Default` をそのまま使う | 入力値を使う |
| **Saved** | 既存 VRCExpressionParameters の `Saved` をそのまま使う | チェックボックスの値を使う |
| **Range** | Animator / メニューから値域を自動検出する | `Min` / `Max` を手入力する |
| **Bool Prefix** | 生成 Bool 名の接頭辞に `{Name}_b` を使う | 入力値を接頭辞として使う |

継承を選んでいるのに継承元の Expression Parameter が存在しない場合、`Default` は 0、`Saved` は OFF になります（ビルドログに通知が出ます）。

#### 再スキャン ボタン

CmpInt / CmpFloat の両方にあります。値域の自動検出とパラメータ一覧はキャッシュされており、再計算されるのは「このコンポーネントの項目を編集したとき」「Undo / Redo」「コンポーネントを選択したとき」だけです。

**Animator や Expressions メニューを別ウィンドウで編集した場合は、このボタンで検出をやり直してください。**

---

### 値域の自動検出

`Range` を継承（OFF）にしている場合、ビルド時とインスペクタ表示時に以下を走査して値域を決定します。ビルドパスは Modular Avatar の後に実行されるため、**マージ済みの最終的な Animator とメニュー**が対象になります。

#### 検出できるもの

| ソース | 精度 |
|---|---|
| 遷移条件の `Equals` / `NotEqual` の threshold | 確実 |
| VRCExpressionsMenu の主パラメータの `value` と、解除時に戻る `0`（サブメニュー再帰） | 確実 |
| Parameter Driver の `Set` の value | 確実 |
| Parameter Driver の `Random` の `valueMin` / `valueMax` | 確実 |
| Parameter Driver の `Copy`（Convert Range 使用時）の `destMin` / `destMax` | 確実 |
| BlendTree の threshold（Int を blend parameter に使う場合、入れ子も再帰） | 確実 |
| 解決後の Default 値 | 確実 |

走査対象は全プレイアブルレイヤーのコントローラで、サブステートマシンも再帰的に辿ります。AnyState / Entry / State / StateMachine 遷移のすべての条件を見ます。

メニューの OFF / 解除で戻る `0` も含めます。たとえば Default = 1、Toggle の value = 1 でも、自動検出する値域は `0〜1` です。Puppet の `subParameters` が取る連続値の範囲は、このメニュー走査では推定しません。

#### 検出できないもの

| ソース | 挙動 |
|---|---|
| 遷移条件の `Greater` / `Less` | 境界値とその隣を範囲に含めた上で**警告**。真の上限・下限は静的に決まらない |
| Parameter Driver の `Add`（累積） | **警告**。値域が静的に決まらない |
| Parameter Driver の `Copy`（Convert Range 未使用） | **警告**。同上 |
| OSC / 外部ツールからの書き込み | 検出不可能 |

> **注意**
> 上限を取り違えると、範囲外の値は**値域の端にクランプされて同期されます**（ローカルの値とリモートの値が食い違ったままになります）。警告が出た場合、または OSC でこのパラメータを操作する場合は、`Range` を ON にして手入力してください。

値が 1 つも検出できなかった場合はエラーになり、そのエントリは展開されません。

---

### ビルド時の処理

[ビルド時の共通処理](#ビルド時の共通処理)のタイミングで実行されます。

#### 1. VRCExpressionParameters の書き換え

| パラメータ | Type | Synced | Saved | Default |
|---|---|---|---|---|
| `{Name}`（CmpInt 本体） | Int | **OFF**（強制） | 解決結果に従う | 解決結果に従う |
| `{Name}_b0` … `{Name}_b(N-1)` | Bool | **ON** | **OFF** | Default 値のビット |

CmpInt 本体は元の `Synced` チェックの状態に関わらず**必ず非同期**（0 bit）になります。同期は生成された Bool が担当します。

生成 Bool は Int の値が変わるたびに Animator が Set し直すため保存する必要がなく、`Saved` は常に OFF です。

#### 2. 変換レイヤーの生成（FX のみ）

値ごとに State を 1 つ生成し、AnyState から条件付きで飛ばします。遷移設定は[生成されるレイヤーの共通仕様](#生成されるレイヤーの共通仕様)に従います。

| レイヤー | 向き | AnyState からの遷移条件 | State の Driver |
|---|---|---|---|
| `WNAE/Enc/{Name}` | ローカル：Int → Bool | `IsLocal == true` かつ `{Name} Equals {値}` | `Local Only = ON`。各 Bool をビット値に `Set` |
| `WNAE/Dec/{Name}` | リモート：Bool → Int | `IsLocal == false` かつ Bool 群がその値のビットパターンと一致 | `Local Only = OFF`。`{Name}` に値を `Set` |

---

### ビット数の計算

必要ビット数 N は値の個数から求めます。

| 値の個数 | 必要ビット数 | 節約 |
|---|---|---|
| 1〜2 | 1 bit | 7 bit |
| 3〜4 | 2 bit | 6 bit |
| 5〜8 | 3 bit | 5 bit |
| 9〜16 | 4 bit | 4 bit |
| 17〜32 | 5 bit | 3 bit |
| 33〜64 | 6 bit | 2 bit |
| 65〜128 | 7 bit | 1 bit |
| 129〜256 | 8 bit | 0 bit（節約なし） |

生成される State 数は、エンコードとデコード合わせて **値の個数 × 2 + 2** です（各レイヤーに待機用の `Wait` State が 1 個ずつ入ります）。

---

### 制限事項

- 値の個数の上限は **256**。超えるとエラーになります
- 値の個数が **64 を超える**と警告が出ます（128 State 以上が生成され、ビルド時間と Animator のサイズに影響します）
- 必要ビット数が 8 bit（節約 0）になる場合は警告が出ます。通常の同期 Int のままにしてください
- リモート側は Bool の到達後に Int が復元されるため、切り替えに数フレームの遅延があります
- `VRCExpressionParameters.ValueType` と `AnimatorControllerParameterType` は SDK / Unity 側の enum のため拡張できません。CmpInt の実体はあくまで**普通の Int** で、メタ情報をこのコンポーネントが持つ構成になっています

---

### CmpInt 固有のエラーと警告

共通のものは[エラーと警告](#エラーと警告)を参照してください。

#### エラー（該当エントリは展開されません）

- 値が 1 つも検出できなかった（`Range` の手入力が必要）
- `Range` 手入力時に `Max` が `Min` 未満
- 値の個数が 256 を超えている

#### 警告（Console のみ）

- `Greater` / `Less` の条件があり真の値域が確定できない
- `Add` / Convert Range 未使用の `Copy` があり値域が静的に決まらない
- 値の個数が 64 を超えている
- 必要ビット数が 8 bit で節約にならない
- `Range` 手入力時に Default が値域外（ビルド時に丸められます）

---

## 2. WNAE CmpFloat Settings

### 概要

VRChat の同期 Float は **-1〜1 を 8 bit で表す固定小数点**（256 段階）です。Float はアニメーションのフレーム指定に使われることが多く、その用途では負の値が不要で、かつ 256 段階もの精度は要りません。

**CmpFloat（精度圧縮 Float）** は、使う値域と必要な精度（ビット数）を指定して、その分だけの同期 `Bool` へ量子化する仕組みです。0〜1 を 4 bit なら 8 bit → 4 bit となり 4 bit 節約できます。

CmpInt と違い、Float は連続値なので**値域の自動検出は行いません**。圧縮は実質「どれだけ精度を落とすか」の選択なので、値域とビット数はどちらも明示指定です。

### 導入

アバタールートに **Add Component → WoodenNut → WNAE CmpFloat Settings** を追加します。CmpInt と同時に使えます。配置のルール（1 GameObject につき 1 個・複数に分割可）は CmpInt と同じです。

### Inspector

Inspector の構成は CmpInt と同じで、`Name` だけが常に表示され、残りは `Options` の Foldout に入ります。

```
Name      [ Blend                  ▼ ]
▶ Options
```

#### Name

対象となる Float パラメータを、**アバターに登録済みの Float パラメータの一覧から選択**します（検索可）。CmpInt と同じく自由入力はできず、取得元も VRCExpressionParameters と MA Parameters / MA Menu Item です。

#### Options

| 項目 | 上書きトグル | 内容 |
|---|---|---|
| **Default** | あり | OFF なら既存 VRCExpressionParameters の `Default` を継承 |
| **Saved** | あり | OFF なら既存 VRCExpressionParameters の `Saved` を継承 |
| **Range** | **なし（常時有効）** | 圧縮する値域。既定は `Min = 0` / `Max = 1` |
| **Bits** | **なし（常時有効）** | 圧縮後のビット数。既定は `4` |
| **Bool Prefix** | あり | OFF なら `{Name}_b` |

`Range` と `Bits` は常に指定が必要なため、上書きトグルを持ちません。

- `Range` は **-1〜1 の範囲**で指定します。範囲外、または `Min >= Max` はエラーです
- `Bits` は **2〜7** で指定します。1 bit は Bool と変わらず、8 bit は無圧縮と同じなのでこの範囲に制限しています。範囲外はエラーです

---

### 量子化

段階数は `2^Bits`、両端を含む正規化です。

```
段階数  = 2^Bits
間隔    = (Max - Min) / (段階数 - 1)
値(k)   = Min + k × 間隔              k = 0 … 段階数-1
```

`Min = 0` / `Max = 1` / `Bits = 4` の場合（16 段階）:

| ビット | 値 | ビット | 値 |
|---|---|---|---|
| `0000` | 0 | `1000` | 0.5333 |
| `0001` | 0.0666 | `1001` | 0.6 |
| `0010` | 0.1333 | `1010` | 0.6666 |
| `0011` | 0.2 | `1011` | 0.7333 |
| `0100` | 0.2666 | `1100` | 0.8 |
| `0101` | 0.3333 | `1101` | 0.8666 |
| `0110` | 0.4 | `1110` | 0.9333 |
| `0111` | 0.4666 | `1111` | 1 |

| Bits | 段階数 | 分解能（0〜1 の場合） | 節約 | 生成 State 数 |
|---|---|---|---|---|
| 2 | 4 | 0.3333 | 6 bit | 10 |
| 3 | 8 | 0.1429 | 5 bit | 18 |
| 4 | 16 | 0.0667 | 4 bit | 34 |
| 5 | 32 | 0.0323 | 3 bit | 66 |
| 6 | 64 | 0.0159 | 2 bit | 130 |
| 7 | 128 | 0.0079 | 1 bit | 258 |

生成 State 数は `段階数 × 2 + 2`（エンコード / デコードの 2 レイヤー + 各レイヤーの `Wait` State）です。

---

### ローカルとリモートの差

**ローカル側の Float は量子化されません。** 連続値のまま使われ、リモート側だけが `2^Bits` 段階になります。

これは VRChat の標準挙動と同じ性質です。素の同期 Float もローカルは full precision、リモートは 8 bit（256 段階）で、CmpFloat はリモート側の粒度を 256 段階から `2^Bits` 段階へ落としているだけです。

ローカルも量子化して完全に一致させる方式も考えられますが、Radial Puppet がメニュー操作中に毎フレーム値を書き込むため、Driver で量子化値を書き戻すと発振します。そのため採用していません。

---

### ビルド時の処理

[ビルド時の共通処理](#ビルド時の共通処理)のタイミングで実行されます。

#### 1. VRCExpressionParameters の書き換え

| パラメータ | Type | Synced | Saved | Default |
|---|---|---|---|---|
| `{Name}`（CmpFloat 本体） | Float | **OFF**（強制） | 解決結果に従う | **量子化後**の値 |
| `{Name}_b0` … `{Name}_b(Bits-1)` | Bool | **ON** | **OFF** | Default 値のビット |

Default はエンコード結果と食い違わないよう、量子化後の値にそろえられます。隣り合う段階の中点ちょうどは、低い方の段階に割り当てます。

#### 2. 変換レイヤーの生成（FX のみ）

**`WNAE/Enc/{Name}`（エンコード：ローカルで Float → Bool）**

Float には `Equals` 条件が使えないため、各段階を **下限より大きく、上限以下**の区間に分けます。どの入力値も 1 つの段階だけに一致するので、遷移順や自己遷移の除外によって別の段階へ移ることはありません。

```
閾値(k) = 値(k) - 間隔/2

IsLocal == true に加えて、次の条件で振り分ける（0〜1、4 bit の場合）：

k=15: Blend > 閾値(15)
k=14: Blend > 閾値(14)  かつ  Blend <= 閾値(15)
   :
k=1 : Blend > 閾値(1)   かつ  Blend <= 閾値(2)
k=0 : Blend <= 閾値(1)
```

Animator には `<=` がないため、上限判定は `Less(閾値の次に大きい float 値)` で表現します。一定の誤差幅を足す方式ではなく、隣の表現可能値を使うことで、**中点に穴や区間の重複を作らず**低い段階へ含めます。最下段には下限、最上段には上限を設けず、Range 外の入力も端の段階に割り当てます。

各 State に `VRC Avatar Parameter Driver`（`Local Only = ON`）を付け、段階のビットパターンを Bool 群に `Set` します。

**`WNAE/Dec/{Name}`（デコード：リモートで Bool → Float）**

- 段階ごとに State を 1 つ生成し、`VRC Avatar Parameter Driver`（`Local Only = OFF`）で `{Name}` に量子化後の値を `Set`
- AnyState からの遷移条件：`IsLocal == false` かつ Bool 群がその段階のビットパターンと一致

---

### CmpFloat 固有のエラーと警告

共通のものは[エラーと警告](#エラーと警告)を参照してください。

#### エラー（該当エントリは展開されません）

- `Range` が -1〜1 の範囲外
- `Min >= Max`
- `Bits` が 2〜7 の範囲外
- Range / Default に NaN または無限大が設定されている
- Range が狭すぎて、指定した量子化段階を float で区別できない

#### 警告（Console のみ）

- `Bits` が 6 以上（128 State 以上が生成される）
- Default が値域外（ビルド時に丸められます）

---

## 3. WNAE Parameter Calculation

### 概要

VRChat の `VRC Avatar Parameter Driver` は `Add` が**定数加算しかできず**、パラメータ同士の演算を行う公式手段がありません。そのため通常は State と遷移条件を大量に手組みする必要があり、現実的に書けません。

**WNAE Parameter Calculation** は State に Add Behaviour するだけで `c = a op b` を宣言できます。ビルド時にその State は Sub State Machine へ置き換えられ、Parameter Driver と遷移条件の組み合わせに展開されます。

### 導入

Animator の State を選択し、**Add Behaviour → WNAE Parameter Calculation** を追加します。1 つの State に複数追加すると、リスト順に直列実行されます。

対象は FX に限らず、どのプレイアブルレイヤーのコントローラでも構いません。

> **この State は演算専用になります。** ビルド時に State ごと Sub State Machine へ置き換えられるため、
> **Motion や他の Behaviour を設定しても失われます**。アニメーションを再生したい場合は別の State を用意してください。

---

### Inspector

| 項目 | 内容 |
|---|---|
| **Operation** | 演算種別（14 種） |
| **A (入力)** | 入力 1 |
| **B (入力)** | 入力 2。`NOT` では使われないためグレーアウトされます |
| **C (出力)** | 出力 |
| **Bit Width** | 演算のビット幅（1〜8、既定 8） |

A / B / C は、**開いている Animator ウィンドウのコントローラから Int パラメータを列挙**して選択します（VRChat SDK の Parameter Driver と同じ方式）。取得できない場合は手入力にフォールバックします。一覧が古い場合は「パラメータ一覧を再取得」ボタンで更新してください。

**A・B・C には同じパラメータを指定できます。** 展開の先頭で a と b を中間パラメータへコピーしてから演算するため、`a = a + a` のような書き方をしても途中で壊れません。

---

### 演算種別

ビット幅を N、`mod = 2^N` とします。

| 演算 | 内容 |
|---|---|
| **Add** | `c = a + b`。和が `mod` 以上なら `c % mod` |
| **Sub** | `c = a - b`。差が負なら `c + mod` |
| **Mul** | `c = a * b`。積が `mod` 以上なら `c % mod` |
| **Div** | `c = a / b`（切り捨て） |
| **Mod** | `c = a % b` |
| **NOT** | `c = ~a`（N ビット幅で反転）。b は無視 |
| **AND** | `c = a & b` |
| **OR** | `c = a \| b` |
| **XOR** | `c = a ^ b` |
| **XNOR** | `c = ~(a ^ b)`（N ビット幅） |
| **L-SHIFT** | `c = (a << b) & (mod - 1)`。`b >= N` は `c = 0` |
| **R-SHIFT** | `c = a >> b`。`b >= N` は `c = 0` |
| **L-ROTATE** | `c = a` を左に `b` ビット回転。押し出されたビットは右端へ回り込む |
| **R-ROTATE** | `c = a` を右に `b` ビット回転。押し出されたビットは左端へ回り込む |

**ゼロ除算**: `Div` / `Mod` ともに `b = 0` のときは `c = 0` になります。

**回転量**: 回転は N ビットで一周するため、`b` は `b mod N` として扱われます（`N = 8` なら `b = 8` は無回転、`b = 9` は 1 ビット回転）。シフトと違い `b >= N` でも `c = 0` にはなりません。

```
N = 8, a = 142 (1000 1110), b = 1

L-ROTATE →  29 (0001 1101)
R-ROTATE →  71 (0100 0111)
```

> 剰余は **256 ではなく `2^BitWidth`** です。Bit Width が 8 なら `% 256`、4 なら `% 16` になります。

---

### Bit Width と生成 State 数

**Bit Width は生成される State 数を大きく左右します。** 実際に使う値域に合わせて絞ってください。Inspector に生成 State 数が表示されます。

| 演算 | N=4 | N=6 | N=8 |
|---|---|---|---|
| Add / Sub | 8 | 10 | 12 |
| NOT | 6 | 8 | 10 |
| AND / OR / XOR / XNOR | 18 | 26 | 34 |
| L-SHIFT | 10 | 14 | 18 |
| R-SHIFT | 17 | 30 | 47 |
| L-ROTATE / R-ROTATE | 25 | 49 | 80 |
| Mul | 20 | 33 | 54 |
| **Div / Mod** | **44** | **186** | **760** |

512 State を超える設定では警告が出ます。

---

### 実行にかかるフレーム数

Parameter Driver は **State 進入時にしか実行されない**ため、演算は 1 フレームでは完結しません。

展開されたチェーンは「立っているビットへ直接跳ぶ」構造なので、所要フレームは入力の**立っているビットの数**で決まります。以下は全入力を通したときの最悪値（括弧内は平均）です。

| 演算 | N=4 | N=6 | N=8 |
|---|---|---|---|
| NOT / AND / OR / XOR / XNOR | 6 | 8 | 10 |
| L-SHIFT | 6 (3.2) | 8 (3.1) | 10 (3.1) |
| Add / Sub | 7 (5.0) | 9 (6.0) | 11 (7.0) |
| R-SHIFT | 7 (3.3) | 9 (3.2) | 11 (3.1) |
| Div / Mod | 7 (3.6) | 9 (3.7) | 11 (3.7) |
| L-ROTATE / R-ROTATE | 9 (6.0) | 12 (7.5) | 16 (9.5) |
| Mul | 12 (7.5) | 17 (10.1) | 21 (12.6) |

AND / OR / XOR / XNOR は全ビットを必ず走査するため、平均も最悪値と同じです。それ以外は平均が最悪値をかなり下回ります（Bit Width 8 の最悪 11 フレームは 90fps で約 0.12 秒）。

- 元の State から出ていく遷移は演算チェーンの終端に移されるため、**演算が終わってから評価されます**
- **State に入った直後に c を読む使い方はできません**

---

### ビルド時の展開

[ビルド時の共通処理](#ビルド時の共通処理)の**最初、Modular Avatar より前**に実行されます。生成された公式 Parameter Driver の参照名に MA のリネームが適用された後、CmpInt の値域検出が行われます。

**Behaviour が付いた State は、演算用の Sub State Machine そのものに置き換えられます。**

```
Sub State Machine "WNAE Calc/{元の State 名}"
   [Init]   入力を中間パラメータへコピー         ← 元の State を指していた遷移がここに向く
   [演算チェーン]
   [Store]  結果を c へコピー                    ← 元の State の出ていく遷移がここに移る
```

- **State に設定されていた Motion と他の Behaviour は失われます**（置き換えのため）。該当する場合は Console に警告が出ます
- 元の State を指していた遷移は、AnyState / Entry / State / StateMachine のすべてを走査して `Init` に張り替えられます。自己遷移も `Init` に向くため、ループして再計算する使い方ができます
- AnyState 遷移の `Can Transition To Self = OFF` は、`Init` だけでなく**置き換え後のチェーン全体**に対して維持します。条件が ON のままでも計算を最初からやり直さず、完了後も終端で待機できます。別の State へ出た後は再び進入でき、別 State への AnyState 割り込みも妨げません。`ON` を指定した遷移は、元の再進入を許可する設定のままです
- 元の State が Default State だった場合は、Entry から Sub State Machine へ入る遷移が追加されます。条件なしの Entry 遷移は常に成立するため、既存の条件付き Entry 遷移を遮らないよう**末尾**に置かれます（Default State の「どれにも該当しないとき」の役割を引き継ぎます）
- 元の State に **Exit へ抜ける遷移**があった場合は、Sub State Machine から親へ Exit を伝播する遷移が自動で追加され、「親のステートマシンから抜ける」という元の意味が保たれます

#### 演算の仕組み

`Add` が定数加算しかできないため、**入力の立っているビットを消しながら acc に定数を足し込む**チェーンが基本形です。各レベルの State へは「最上位の立っているビットが k」という範囲条件 2 つで**それまでのどの State からも直接跳び込める**ため、立っていないビットの分の State を通りません（State 数はビット幅ぶんだけ、所要フレームは立っているビット数だけで済みます）。

```
level k への遷移条件:  value > 2^k - 1  かつ  value < 2^(k+1)
level k の Driver   :  value -= 2^k ; acc += 2^k（演算ごとの定数）
```

すべての遷移は互いに排他な明示条件を持つので、遷移の並び順には依存しません。また、入力が Bit Width の範囲外でも行き場を失わないよう、受け皿の遷移が張られています（結果は保証されません）。

`Div` / `Mod` は b の値ごとに分岐すると除数が定数になるため、`b×2^k` を引けるだけ引く二分長除算に落とせます。商が acc に、剰余が ta に残るので同じチェーンで両方求まります。

`Mul` は b を上位 / 下位の 2 ブロックに分け、それぞれ `Copy` の **Convert Range による定数倍**で部分積を作ってから足し合わせます（`L-SHIFT` も同じ定数倍を使います）。変換元の範囲は `0〜2^N` にしています。2 の冪なので浮動小数点でも除算が厳密で、入力が必ず変換元範囲に収まるため、**クランプの有無や Int への丸め規則に依存せず**結果が厳密な整数になります。

---

### 中間パラメータ

`WNAE/Calc/{連番}/ta` ・ `/tb` ・ `/acc`（Mul のみ `/t2` も）という Int パラメータのうち、演算が使うものだけが Animator に追加されます。

| 演算 | 追加される中間パラメータ |
|---|---|
| Add / Sub | `tb` / `acc` |
| NOT | `ta` / `acc` |
| Mul | `ta` / `tb` / `acc` / `t2` |
| 上記以外 | `ta` / `tb` / `acc` |

- **VRCExpressionParameters には追加されません**（同期不要）
- 追加先は、その Behaviour が存在するコントローラのみです
- 既存パラメータと名前が衝突する連番は飛ばします。生成名をユーザー側のパラメータとして使わないでください

自己遷移を禁止した AnyState 遷移から入る場合、その Animator レイヤーに `WNAE/State/{連番}/active` という非同期 Int が 1 個追加されます。レイヤー内の各 State の Parameter Driver で現在の所属を記録し、チェーン全体への再進入を防ぎます。追加の State や待ちフレームは発生しません。この仕組みは Encoder / Decoder にも共通です。

A / B / C に指定したパラメータがコントローラに存在しない場合は Int として追加されます。Int 以外の型で存在していた場合は警告が出ます。

---

### 出力を同期パラメータにする場合の注意

**演算結果 `c` に同期パラメータを指定した場合の挙動は未検証です。**

展開されたチェーンの Parameter Driver は `Local Only = OFF` で、ローカルとリモートの両方で実行されます。中間パラメータ（`ta` / `tb` / `acc`）は同期されないため、各クライアントが自分で計算する必要があり、この設定自体は必要なものです。

ただし `c` が同期パラメータの場合、所有者が計算した値がネットワーク同期される一方で、リモート側も自分で計算した値を書き込みます。入力も同期パラメータであれば最終的には同じ値に収束するはずですが、**計算にかかる数フレームの間に食い違う可能性があり、実機で確認できていません。**

確実を期すなら、`c` には非同期パラメータ（CmpInt / CmpFloat の本体など）を指定し、同期が必要な場合は CmpInt で圧縮して同期させてください。

---

### CmpInt との組み合わせ

演算結果 `c` を CmpInt で圧縮する場合、**CmpInt の値域自動検出は演算結果を追えません**（`Copy` で書き込まれるため）。ビルド時に「値域を静的に決められない Parameter Driver がある」という警告が出るので、`Range Override` で値域を手入力してください。

---

### Parameter Calculation 固有のエラーと警告

#### エラー（該当 Behaviour は展開されません）

- Parameter A / B / C が指定されていない
- Bit Width が 1〜8 の範囲外

#### 警告（Console のみ）

- 生成 State 数が 512 を超える
- Behaviour を付けた State に Motion や他の Behaviour が設定されている（置き換えで失われる）

---

## 4. WNAE Parameter Encoder / Decoder

### 概要

**8 個の Bool** と **1 個の Int** を相互に変換する 2 つの Behaviour です。

| Behaviour | 向き |
|---|---|
| **WNAE Parameter Encoder** | Bool ×8 → Int（ビット列を 1 バイトにまとめる） |
| **WNAE Parameter Decoder** | Int → Bool ×8（1 バイトをビット列に分解する） |

Parameter Calculation と同様、State に Add Behaviour すると、その State はビルド時に Sub State Machine へ置き換えられます。

### 導入

Animator の State を選択し、**Add Behaviour → WNAE Parameter Encoder**（または **Decoder**）を追加します。

**Parameter Calculation と同じ State に混ぜて置くこともでき、その場合はリスト順に直列実行されます。** 「Int をビットに分解 → ビットごとに演算 → Int に戻す」といった処理を 1 つの State にまとめられます。

> **この State は変換専用になります。** Parameter Calculation と同じく、**Motion や他の Behaviour を設定しても失われます**。

---

### ビットの並び

**`Bit 1` が LSB（`2^0`）、`Bit 8` が MSB（`2^7`）** です。CmpInt の `_b0` が LSB という規則と同じ並びです。ただし、圧縮用の生成 Bool 名は WNAE 専用です。Encoder / Decoder には別途 Animator に登録した Bool を選択してください。圧縮用 Bool を事前に同名で登録すると、ビルド時の衝突検査でエラーになります。

```
Bit 8  Bit 7  Bit 6  Bit 5  Bit 4  Bit 3  Bit 2  Bit 1
 128     64     32     16      8      4      2      1
```

---

### Inspector（Encoder）

| 項目 | 内容 |
|---|---|
| **出力 (Int)** | エンコード結果の出力先 |
| **Bit 1 (LSB)** 〜 **Bit 8 (MSB)** | 各ビットの入力元 |

各ビットは **`0 固定` / `1 固定` / Bool パラメータ** から選びます。既定は `0 固定` です。

`0 固定` と `1 固定` は分岐を作らず定数として処理されるため、**使うビットが少ないほど生成 State 数が減ります**。

### Inspector（Decoder）

| 項目 | 内容 |
|---|---|
| **入力 (Int)** | デコード対象 |
| **Bit 1 (LSB)** 〜 **Bit 8 (MSB)** | 各ビットの出力先 Bool。**`（非選択）` にするとそのビットは出力されません** |

---

### 生成 State 数

| Behaviour | 数式 | 例 |
|---|---|---|
| Encoder | `2 × (パラメータ指定のビット数) + 2` | 8 ビットすべてパラメータ → 18 |
| Decoder | `(8 - 最下位の選択ビット) + 2` | Bit 1 まで選択 → 10 / Bit 5 以上のみ選択 → 6 |

Decoder は、選択されていないビットも**それより下のビットを正しく取り出すために分解の対象になります**。そのため、下位ビットを選ぶほど State が増えます。逆に上位ビットだけ使う場合は短く済みます。

Inspector に生成 State 数が表示されます。

---

### ビルド時の展開

[ビルド時の共通処理](#ビルド時の共通処理)で、Parameter Calculation と同じ仕組みで展開されます。

**Encoder**

```
Sub State Machine "WNAE Encoder/{元の State 名}"
   [Init]   Set acc = (1 固定ビットの合計)
   [各パラメータビットの 2 分岐]  Bool が true なら acc += 2^i
   [Store]  Copy acc → 出力 Int
```

**Decoder**

```
Sub State Machine "WNAE Decoder/{元の State 名}"
   [Init]   Copy 入力 Int → ta ; 選択された出力 Bool をすべて false に
   [Bit 8 → 最下位の選択ビット]  立っているビットへ直接跳び、ta -= 2^k ; 出力 Bool = true
   [Done]   （元の State の出ていく遷移がここに移る）
```

Decoder は演算チェーンと同じ「立っているビットへ直接跳ぶ」構造なので、State はビット位置ごとに 1 つで、立っていないビットにはフレームを消費しません。選択された出力は Init で一旦 false になり、立っているビットだけが後から true になります（完了までの数フレームは過渡状態です）。

中間パラメータは `WNAE/Encoder/{連番}/acc`・`WNAE/Decoder/{連番}/ta` という名前で Animator に追加されます。**VRCExpressionParameters には追加されません**（同期不要）。

---

### Encoder / Decoder 固有のエラーと警告

#### エラー（該当 Behaviour は展開されません）

- Encoder: 出力 Int が指定されていない
- Encoder: ビットが Bool パラメータ指定なのにパラメータが選ばれていない
- Decoder: 入力 Int が指定されていない
- Decoder: 出力先の Bool が 1 つも選択されていない

#### 警告（Console のみ）

- 指定したパラメータが期待と異なる型で存在している
- Decoder: 同じ Bool が複数のビットに割り当てられている（どちらかのビットが立っていれば true になります）

---

### 制限事項

- **Decoder の入力は 0〜255 を想定しています。** 範囲外の値（256 以上・負数）では正しいビット列は得られません（現在の実装では全ビット false になりますが、保証はしません）
- Encoder は **パラメータ指定のビット数 + 2 フレーム**、Decoder は **立っているビットの数 + 2 フレーム**程度で完了します（Parameter Driver が State 進入時にしか動かないため 1 フレームでは完結しません）

---

## ビルド時の共通処理

NDMF の `Transforming` フェーズで、**Behaviour の展開 → Modular Avatar → パラメータ圧縮**の順に処理します。

### 1. Behaviour の展開（MA より前）

`Expand WNAE behaviours` パス（プラグイン ID: `wooden-nut.wnae.behaviours`）は `BeforePlugin("nadena.dev.modular-avatar")` を指定しています。

- Parameter Calculation / Encoder / Decoder を、公式 Parameter Driver と遷移条件へ展開します
- MA Merge Animator の**未マージのコントローラ**も対象です
- 1 つの State に複数の WNAE Behaviour がある場合は、種類を問わずリスト順に直列実行します
- MA は任意の独自 Behaviour の文字列を書き換えないため、**公式 Driver に展開してから MA のリネームを受ける**必要があります

### 2. パラメータ圧縮（MA より後）

`Expand WNAE parameters` パス（プラグイン ID: `wooden-nut.wnae`）は `AfterPlugin("nadena.dev.modular-avatar")` を指定しています。リネーム・Animator マージ・メニュー結合後の最終形を走査します。

**CmpInt / CmpFloat は同じ圧縮パスで処理し、VRCExpressionParameters の複製とビット予算の検査を 1 回にまとめています。** この段階では Behaviour が公式 Driver へ展開済みなので、計算結果の書き込みを「値域を静的に決められない Copy」として検出できます。

1. `WNAE CmpInt Settings` / `WNAE CmpFloat Settings` を収集
2. それぞれ値域・継承を解決し、検証結果を Console に出力
3. パラメータ名と生成 Bool 名の重複・衝突を**両方の型をまたいで**検査
4. `VRCExpressionParameters` を **1 回だけ**クローンし、両方の結果を書き込む
   - 元のアセットは複製されるため、プロジェクト内のアセットは変更されません
   - 展開後の合計が 256 bit を超える場合はエラー
5. 元パラメータと生成 Bool 群を**全プレイアブルレイヤーのコントローラ**（Base / Additive / Gesture / Action / FX / Sitting / TPose / IKPose）に宣言
   - これにより、FX で圧縮した結果の Bool を他のレイヤーからも同じ名前で参照できます
6. 変換レイヤーを **FX にのみ**生成（FX が無い場合は新規作成）
7. `WNAE CmpInt Settings` / `WNAE CmpFloat Settings` を削除（独自 Behaviour は前段の展開時に取り除かれます）

### 生成されるレイヤーの共通仕様

CmpInt / CmpFloat の**変換レイヤー**（AnyState 遷移）の設定は次の通りです。

- `Has Exit Time = OFF` / `Duration = 0` / `Can Transition To Self = OFF`
  - `Can Transition To Self` を OFF にしているのは、Driver が State 進入時にのみ実行されるためです。ON だと毎フレーム再発火します
- State のモーションは空クリップ（`WNAE Empty`）で、何もアニメートしません

Behaviour の**展開チェーン**（State 間の遷移）の設定は次の通りです。

- `Duration = 0` / `Fixed Duration = ON`
- 条件付きの遷移は `Has Exit Time = OFF`。条件を持たない遷移だけは `Has Exit Time = ON` / `Exit Time = 0` にしています
  - 条件も Exit Time も無い遷移は Unity では**永久に成立しない**ため、無条件に次へ進みたい箇所では Exit Time 0 が必要です
- State のモーションは空クリップ（`WNAE Expand Empty`）です

両者に共通する事項は次の通りです。

- `Write Defaults` は変換レイヤーでは対象コントローラの既存 State の多数派に、展開チェーンでは置き換え元の State の値に合わせます（混在による VRChat の警告を避けるため）
- `IsLocal` は VRChat 組み込みパラメータで、FX に無い場合のみ追加されます。VRCExpressionParameters には追加しません

---

## 他ツールとの関係

| ツール | 関係 |
|---|---|
| **Modular Avatar** | Behaviour を MA より前に公式 Driver へ展開し、MA Parameters のリネームを受けます。CmpInt / CmpFloat は MA より後に最終名とマージ済みの内容を使って圧縮します |
| **Avatar Optimizer** | `Optimizing` フェーズなので本パスより後に実行されます。影響ありません |
| **VRCExpressionsMenu** | パラメータ名は変わらないため、既存のメニュー（Toggle / Radial / SubMenu）はそのまま動作します。非同期パラメータでもメニューからのローカル操作は可能で、それをエンコードレイヤーが同期します |
| **OSC** | 非同期パラメータも OSC から書き込めるため、OSC 経由の操作もエンコードレイヤー経由で同期されます。ただし CmpInt の**値域の自動検出は OSC を見られない**ため、`Range` の手入力が必要です |

---


## エラーと警告

Inspector には**エラーのみ**表示されます（Calculation / Encoder / Decoder の「生成 State 数」の表示だけは例外で、Bit Width や使用ビットを決める判断材料として常に出ます）。警告と情報はビルド時に Console へ出力されます（`[WNAE]` 接頭辞）。

**エラーが 1 件でもあるとアップロードがブロックされます。** NDMF のエラーレポートに `Error` として登録されるため、Console を見ていなくても SDK 側で止まります。設定した圧縮が無言で適用されないまま出力されることはありません。

### CmpInt / CmpFloat 共通のエラー（該当エントリは展開されません）

- パラメータ名が空
- **対象のパラメータが最終的なアバターに存在しない**（Modular Avatar のリネーム先を後から変更した場合など。項目を選び直してください）
- パラメータ名が他のエントリと重複している（CmpInt / CmpFloat をまたいで検査）
- 生成される Bool 名が既存パラメータ、または他のエントリの本体名・生成 Bool 名と衝突している（`Bool Prefix` で回避）。既存名は VRCExpressionParameters だけでなく Animator の Parameters も検査します。エントリ同士の衝突は順番に関係なく両方をエラーにします
- 展開後の同期パラメータが 256 bit を超えている

機能ごとの固有エラーは [CmpInt](#cmpint-固有のエラーと警告) / [CmpFloat](#cmpfloat-固有のエラーと警告) / [Parameter Calculation](#parameter-calculation-固有のエラーと警告) / [Encoder / Decoder](#encoder--decoder-固有のエラーと警告) を参照してください。

### 情報（Console のみ）

- 解決した値域・ビット数（エントリごとに毎回出力）
- 継承元の Expression Parameter が無いため Default = 0 / Saved = OFF になった
- 展開後の同期パラメータの合計ビット数

---

## 既知の制限・未検証事項

以下は β1 時点のものです。

- **実機（VRChat 上）での動作確認が未了です。** Unity エディタ上での生成結果と、`Tests/` のマネージド遷移モデルによる検証（198,258 assertions）までは確認済みですが、アップロードして複数人で同期させた状態での検証は行っていません
- **演算の網羅検証は Bit Width 1〜4 が全入力、5〜8 は境界サンプルです。** 全ビット幅の全入力を突き合わせているわけではありません（`Tests/WNAEModelTests.cs`）
- **2026/09/05 および 2026/09/13 の修正は、Unity / SDK / NDMF の参照を使ったコンパイルと、実ソースの生成処理を動かすマネージド遷移モデルで検証しています。** Float の中点と隣接値、AnyState の条件保持・割り込み・再進入、名前衝突、大きな Range などを検査していますが、今回の変更後の Unity ネイティブ遷移評価と実際の MA ビルドを含む統合テストは未了です
- **`Copy` の Convert Range の実装詳細（クランプの有無・Int への丸め規則）が VRChat SDK のソースから確認できません。** そのため、変換元範囲を 2 の冪（`0〜2^N`）にして入力が必ず範囲内に収まるようにし、結果が浮動小数点でも厳密な整数になる定数倍（`Mul` / `L-SHIFT`）に限定しています。この構成ではクランプ・丸めのどちらの仕様でも結果が変わらないはずですが、実機未確認である点は変わりません
- **CmpInt の値域自動検出は、OSC や Parameter Calculation の演算結果を追えません。** 該当する場合は `Range` の手入力が必要です
- **極端に大きな Int の値域は正しく圧縮できません。** Animator の遷移条件と Parameter Driver の値は float で保持されるため、`2^24`（16,777,216）を超える整数は 1 つずつ区別できません。値の個数が 256 以下でも、`Range` に `16777216`〜`16777217` のような値域を手入力すると、エンコード・復元のどちらも失敗します。現在この検査は行っていないので、大きな値を使う場合は値域を `2^24` 未満に収めてください
- **Modular Avatar のリネーム先を WNAE の中間パラメータ名と同じにするとビルドが失敗します。** `WNAE/Calc/{連番}/acc` のような名前へ MA でリネームすると、パラメータ名が重複して MA 側で例外が発生します。`WNAE/` で始まる名前を MA のリネーム先に指定しないでください
- **アセットが壊れている場合、演算種別が別のものとして実行されることがあります。** `Operation` やビットの入力元に未知の値が保存されていると、エラーにならず Div（除算）や `0 固定` として扱われます。YAML を手編集した場合や、新しい版で保存したアセットを古い版で開いた場合に起こり得ます
- **実行時に Float パラメータへ NaN が書き込まれると、その CmpFloat の同期が止まります。** すべての遷移条件が成立しなくなるため、生成 Bool が直前の値のまま固定されます。有限値が書き込まれれば復帰します。OSC など外部からの入力で NaN を送らないでください
- Div / Mod は Bit Width 8 で 1 個あたり約 760 State を生成します。多用する場合は Bit Width を絞ってください

---

## 更新履歴

### β1（2026/09/13）

**修正**

- **CmpInt の値域外の入力で同期が止まる問題を修正。** 値域を外れた値は端へクランプされるようになりました（CmpFloat と同じ扱い）。従来は生成 Bool が直前の値のまま固まり、リモートとの不一致が解消されませんでした
- **エラーがあってもビルドが成功してしまう問題を修正。** エラーは NDMF のエラーレポートに登録され、**アップロードがブロックされます**。同期パラメータが 256 bit を超えた場合や、圧縮対象が見つからない場合に、設定が無言で無視されたまま公開されることはなくなりました
- **Calculation / Encoder / Decoder のパラメータ型の不一致をエラーにしました。** 従来は警告のみで展開を続けていたため、Bool に 0〜255 を書くなど結果が無言で変質する可能性がありました
- **Modular Avatar のリネーム先を後から変更した場合にエラーを出すようにしました。** 従来は古い名前のパラメータを新規作成し、本来の対象が無圧縮のまま残っていました

**変更**

- **エラー時の挙動が変わりました。** 従来は該当項目だけを飛ばしてビルドを続行していましたが、β1 からはアップロードを止めます

**文書**

- 検証範囲の記載を実際のテスト内容に合わせました。従来の「全入力組み合わせ 約 148 万件」は現行のテストを表していませんでした
- 演算結果を同期パラメータに指定した場合の注意と、既知の制限 4 件を追記しました

### α2 修正（2026/09/05）

- CmpFloat の入力が一定でも、生成 Bool が隣の段階と交互に切り替わる問題を修正。中点の扱いを低い段階へ統一
- AnyState の条件が ON のままだと、展開した Behaviour が再進入を繰り返して完了しない問題を修正
- 生成 Bool と既存の Expression / Animator パラメータ、他エントリの本体名との衝突検査を修正
- Behaviour を MA より前に展開し、MA Parameters のリネームに追従するよう変更
- CmpInt のメニュー走査に OFF / 解除時の `0` を追加
- 大きな Int Range のオーバーフロー・無限ループと、Float の非有限値・区別できない量子化段階の検証漏れを修正

### α2（2026/08/15）

**追加**

- **WNAE Parameter Encoder / Decoder** を追加（Bool ×8 ⇔ Int の相互変換）
- Parameter Calculation に **L-ROTATE / R-ROTATE** を追加
- 1 つの State に**種類の違う Behaviour を混在**させて直列実行できるようになりました

**変更**

- Parameter Calculation から **`Copy`（`c = a`。b は無視）を削除**しました
- 生成される Sub State Machine の構造を見直し、**State 数と所要フレーム数を削減**しました（Bit Width 8 の場合：Div / Mod 1262 → 760、Mul 275 → 54、R-SHIFT 83 → 47、L/R-ROTATE 139 → 80、Add / Sub 21 → 12、Decoder 17 → 10）

**修正**

- 置き換え元の State が Default State だったとき、追加される Entry 遷移が既存の条件付き Entry 遷移を遮っていた問題
- 置き換え元の State に Exit へ抜ける遷移があったとき、「親のステートマシンから抜ける」という意味が失われていた問題
- `Mul` / `L-SHIFT` が使う Convert Range の変換元範囲を `0〜1` から `0〜2^N` に変更（クランプの有無や丸め規則に結果が依存しないようにするため）

> **α1 から更新する場合の注意**
> `Operation` は列挙値の順番で保存されます。α1 の `Copy` は列挙値の 12 番目で、α2 以降は同じ位置が **`L-ROTATE`** です。そのため **α1 で `Copy` を選択していた Behaviour は、α2 以降では `L-ROTATE` として読み込まれます。**
>
> α1 の `Copy` は B を使わなかったため、多くの場合 B は未設定のはずです。その場合は「Parameter B が指定されていません」というエラーになり、その Behaviour は展開されません（ビルドログで気づけます）。
>
> ただし **B に値が残っていた場合はエラーになりません。`c = a` のつもりが `c = a を b ビット左回転` として動きます。** α1 から更新する場合は、`Copy` を使っていた Behaviour が無いか確認してください。`Copy` は削除されたので、同じ動作は公式 Parameter Driver の `Copy` に置き換えてください。
>
> α2 から更新する場合、列挙値の変更はありません。

### α1（2026/08/02）

- WNAE CmpInt Settings / WNAE CmpFloat Settings / WNAE Parameter Calculation を公開

---

## ファイル構成

| ファイル | アセンブリ | 内容 |
|---|---|---|
| `WNAE.cs` | Assembly-CSharp | `CmpIntEntry` / `CmpFloatEntry` と値域・ビット計算のユーティリティ、Encoder / Decoder のビット列仕様（`WNAECodec`） |
| `WNAE_CmpIntSettings.cs` | Assembly-CSharp | CmpInt 設定コンポーネント（MonoBehaviour） |
| `WNAE_CmpFloatSettings.cs` | Assembly-CSharp | CmpFloat 設定コンポーネント（MonoBehaviour） |
| `WNAE_ParameterCalculation.cs` | Assembly-CSharp | 演算 Behaviour（StateMachineBehaviour） |
| `WNAE_ParameterEncoder.cs` | Assembly-CSharp | エンコード Behaviour（StateMachineBehaviour） |
| `WNAE_ParameterDecoder.cs` | Assembly-CSharp | デコード Behaviour（StateMachineBehaviour） |
| `Editor/WNAE_Editor.cs` | Assembly-CSharp-Editor | NDMF プラグイン（MA 前後の 2 段階） / 共通ヘルパー（検証・パラメータカタログ・Animator 操作・ExParams・コンポーネント Inspector の基底） |
| `Editor/WNAE_CmpIntEditor.cs` | Assembly-CSharp-Editor | CmpInt の値域スキャナ / 展開 / Inspector |
| `Editor/WNAE_CmpFloatEditor.cs` | Assembly-CSharp-Editor | CmpFloat の展開 / Inspector |
| `Editor/WNAE_BehaviourExpander.cs` | Assembly-CSharp-Editor | State を Sub State Machine へ差し替える共通機構 / チェーン構築部品（`GreedyDispatch` など） / Behaviour Inspector の基底 |
| `Editor/WNAE_ParameterCalculationEditor.cs` | Assembly-CSharp-Editor | 演算チェーンの構築 / Inspector |
| `Editor/WNAE_ParameterCodecEditor.cs` | Assembly-CSharp-Editor | エンコード / デコードチェーンの構築 / Inspector |

3 つの Behaviour（Calculation / Encoder / Decoder）は `IWNAEBehaviourBuilder` を実装してチェーンの構築だけを担当し、State の差し替えと遷移の張り替えは `WNAE_BehaviourExpander.cs` が共通で行います。そのため **1 つの State に種類の違う Behaviour を混在させても、リスト順に直列実行されます**。

`MonoBehaviour` と `StateMachineBehaviour`（いずれも Unity が MonoScript を必要とする型）は、**クラス名とファイル名を一致させないと Add Component / Add Behaviour の一覧に出てきません**。そのためこれらはそれぞれ専用ファイルに分けています。
