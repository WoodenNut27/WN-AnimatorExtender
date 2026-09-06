# WNAE - WN-AnimatorExtender

```
○名称　　　　：WN-AnimatorExtender
○バージョン　：α1
○公開日　　　：2026/08/02
○更新日　　　：2026/08/02
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

VRChat アバター向けの Unity 拡張です。同期パラメータの自動圧縮と、Animator 内での整数演算を提供します。
3 つの機能はいずれも NDMF の単一パスで一括処理されます。

---

## 目次

### 使う

| 節 | 内容 |
|---|---|
| [動作環境とインストール](#動作環境とインストール) | 依存パッケージと導入方法 |
| [1. WNAE CmpInt Settings](#1-wnae-cmpint-settings) | **Int** を、実際に使われている値域から必要最小限のビット数へ圧縮する |
| [2. WNAE CmpFloat Settings](#2-wnae-cmpfloat-settings) | **Float** を、指定した値域と精度（ビット数）へ量子化して圧縮する |
| [3. WNAE Parameter Calculation](#3-wnae-parameter-calculation) | Animator 内で **Int 同士の四則演算・ビット演算**を行う |
| [他ツールとの関係](#他ツールとの関係) | Modular Avatar / Avatar Optimizer / メニュー / OSC との関係 |
| [エラーと警告](#エラーと警告) | エラーの一覧と、どこに出るか |
| [既知の制限・未検証事項](#既知の制限未検証事項) | α1 時点で確認できていないこと |

各機能の節は「概要 → 導入 → Inspector」の順に並んでいます。**使うだけならこの 3 つだけ読めば足ります。**

### 仕組みを知る

使うだけなら読み飛ばして構いません。

| 節 | 内容 |
|---|---|
| [ビルド時の共通処理](#ビルド時の共通処理) | 3 機能に共通する展開の流れと、生成物の仕様 |
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

`WoodenNut/WNAE` フォルダをプロジェクトの `Assets` 以下に配置してください。asmdef を持たないため追加の設定は不要です。

---

## 1. WNAE CmpInt Settings

### 概要

VRChat の同期パラメータ予算は 256 bit で、`Int` は値域に関わらず一律 8 bit を消費します。実際には数個の選択肢しか持たない衣装・髪型スイッチが大半で、大量のビットが無駄になっています。

**CmpInt（可変長圧縮 Int）** は、この Int をビルド時に必要最小限のビット数の同期 `Bool` へ自動分解する仕組みです。値域 0〜5 なら 8 bit → 3 bit となり 5 bit 節約できます。

Animator 上では**普通の Int パラメータのまま**扱われるため、既存の遷移条件やアニメーションの作り方を一切変える必要がありません。

### 導入

アバタールートに **Add Component → WoodenNut → WNAE CmpInt Settings** を追加します。1 つのアバターに 1 個だけ付けられます。

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
| VRCExpressionsMenu の Toggle / Button の `value`（サブメニュー再帰） | 確実 |
| Parameter Driver の `Set` の value | 確実 |
| Parameter Driver の `Random` の `valueMin` / `valueMax` | 確実 |
| Parameter Driver の `Copy`（Convert Range 使用時）の `destMin` / `destMax` | 確実 |
| BlendTree の threshold（Int を blend parameter に使う場合、入れ子も再帰） | 確実 |
| 解決後の Default 値 | 確実 |

走査対象は全プレイアブルレイヤーのコントローラで、サブステートマシンも再帰的に辿ります。AnyState / Entry / State / StateMachine 遷移のすべての条件を見ます。

#### 検出できないもの

| ソース | 挙動 |
|---|---|
| 遷移条件の `Greater` / `Less` | 境界値とその隣を範囲に含めた上で**警告**。真の上限・下限は静的に決まらない |
| Parameter Driver の `Add`（累積） | **警告**。値域が静的に決まらない |
| Parameter Driver の `Copy`（Convert Range 未使用） | **警告**。同上 |
| OSC / 外部ツールからの書き込み | 検出不可能 |

> **注意**
> 上限を取り違えると、範囲外の値がエンコードできず**無言で壊れます**（ローカルではメニューが切り替わるのに、リモートには反映されない）。警告が出た場合、または OSC でこのパラメータを操作する場合は、`Range` を ON にして手入力してください。

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

生成 Bool は毎フレーム Animator が Set し直すため保存する必要がなく、`Saved` は常に OFF です。

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

生成される State 数は、エンコードとデコード合わせて**値の個数 × 2** です。

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

アバタールートに **Add Component → WoodenNut → WNAE CmpFloat Settings** を追加します。1 つのアバターに 1 個だけ付けられます。CmpInt と同時に使えます。

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
| 2 | 4 | 0.3333 | 6 bit | 8 |
| 3 | 8 | 0.1429 | 5 bit | 16 |
| 4 | 16 | 0.0667 | 4 bit | 32 |
| 5 | 32 | 0.0323 | 3 bit | 64 |
| 6 | 64 | 0.0159 | 2 bit | 128 |
| 7 | 128 | 0.0079 | 1 bit | 256 |

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

Default はエンコード結果と食い違わないよう、量子化後の値にそろえられます。

#### 2. 変換レイヤーの生成（FX のみ）

**`WNAE/Enc/{Name}`（エンコード：ローカルで Float → Bool）**

Float には `Equals` 条件が使えないため、**上の段階から順に `Greater` 1 条件だけ**で振り分けます。AnyState 遷移は宣言順で最初にマッチしたものが選ばれるため、これで隙間も重複も生じません。

```
閾値(k) = 値(k) - 間隔/2

k=15: IsLocal == true  かつ  Blend Greater 0.9667
k=14: IsLocal == true  かつ  Blend Greater 0.9000
   :
k=1 : IsLocal == true  かつ  Blend Greater 0.0333
k=0 : IsLocal == true                        ← Float 条件なしの受け皿
```

> 両側を `Greater` + `Less` で挟む方式は採用していません。VRChat の同期 Float は 8 bit 量子化されていて**境界値ちょうどに乗ることが実際に起こり**、`Greater` / `Less` は厳密比較なのでどちらの段階にも入らない穴ができるためです。

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
| **Operation** | 演算種別（13 種） |
| **A (入力)** | 入力 1 |
| **B (入力)** | 入力 2。`NOT` と `Copy` では使われないためグレーアウトされます |
| **C (出力)** | 出力 |
| **Bit Width** | 演算のビット幅（1〜8、既定 8） |

A / B / C は、**開いている Animator ウィンドウのコントローラから Int パラメータを列挙**して選択します（VRChat SDK の Parameter Driver と同じ方式）。取得できない場合は手入力にフォールバックします。一覧が古い場合は「パラメータ一覧を再取得」ボタンで更新してください。

**A・B・C には同じパラメータを指定できます。** 展開の先頭で a と b を中間パラメータへコピーしてから演算するため、`a = a + a` のような書き方をしても途中で壊れません。

---

### 演算種別

ビット幅を N、`mod = 2^N` とします。

| 演算 | 内容 |
|---|---|
| **Copy** | `c = a`。b は無視 |
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

**ゼロ除算**: `Div` / `Mod` ともに `b = 0` のときは `c = 0` になります。

> 剰余は **256 ではなく `2^BitWidth`** です。Bit Width が 8 なら `% 256`、4 なら `% 16` になります。

---

### Bit Width と生成 State 数

**Bit Width は生成される State 数を大きく左右します。** 実際に使う値域に合わせて絞ってください。Inspector に生成 State 数が表示されます。

| 演算 | N=4 | N=6 | N=8 |
|---|---|---|---|
| Copy | 2 | 2 | 2 |
| Add / Sub | 13 | 17 | 21 |
| NOT | 10 | 14 | 18 |
| AND / OR / XOR / XNOR | 18 | 26 | 34 |
| L-SHIFT | 13 | 19 | 25 |
| R-SHIFT | 27 | 51 | 83 |
| Mul | 27 | 79 | 275 |
| **Div / Mod** | **70** | **306** | **1262** |

512 State を超える設定では警告が出ます。

---

### 実行にかかるフレーム数

Parameter Driver は **State 進入時にしか実行されない**ため、演算は 1 フレームでは完結しません。おおむね **N+3 フレーム程度**（Bit Width 8 なら 11 フレーム前後、90fps で約 0.12 秒）かかります。

- 元の State から出ていく遷移は演算チェーンの終端に移されるため、**演算が終わってから評価されます**
- **State に入った直後に c を読む使い方はできません**

---

### ビルド時の展開

[ビルド時の共通処理](#ビルド時の共通処理)の**最初**に実行されます（CmpInt の値域検出より前）。

**Behaviour が付いた State は、演算用の Sub State Machine そのものに置き換えられます。**

```
Sub State Machine "WNAE Calc/{元の State 名}"
   [Init]   Copy a→ta ; Copy b→tb ; Set acc=0   ← 元の State を指していた遷移がここに向く
   [演算チェーン]
   [Store]  Copy acc→c                          ← 元の State の出ていく遷移がここに移る
```

- **State に設定されていた Motion と他の Behaviour は失われます**（置き換えのため）。該当する場合は Console に警告が出ます
- 元の State を指していた遷移は、AnyState / Entry / State / StateMachine のすべてを走査して `Init` に張り替えられます。自己遷移も `Init` に向くため、ループして再計算する使い方ができます
- 元の State が Default State だった場合は、Entry から Sub State Machine へ入る遷移が追加されます

#### 演算の仕組み

`Add` が定数加算しかできないため、**入力を上位ビットから崩しながら acc に定数を足し込む**チェーンが基本形です。分岐は両側に厳密な補集合の条件を持つので、遷移の並び順に依存しません。

```
level k (k = N-1 … 0):
    tb > 2^k - 1  → [b_k=1]  tb -= 2^k ; acc += 2^k
    tb < 2^k      → [b_k=0]  何もしない
```

`Div` / `Mod` は b の値ごとに分岐すると除数が定数になるため、`b×2^k` による二分長除算に落とせます。商が acc に、剰余が ta に残るので同じチェーンで両方求まります。

`Mul` と `L-SHIFT` だけは `Copy` の **Convert Range による定数倍**を使っています。定数倍は除算を含まず結果が厳密な整数になるため、Int への丸め規則に依存しません。

---

### 中間パラメータ

`WNAE/Calc/{連番}/ta` ・ `/tb` ・ `/acc` という Int パラメータが Animator に追加されます。

- **VRCExpressionParameters には追加されません**（同期不要）
- 追加先は、その Behaviour が存在するコントローラのみです

A / B / C に指定したパラメータがコントローラに存在しない場合は Int として追加されます。Int 以外の型で存在していた場合は警告が出ます。

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
- A / B / C が Int 以外の型で存在している
- Behaviour を付けた State に Motion や他の Behaviour が設定されている（置き換えで失われる）

---

## ビルド時の共通処理

3 つの機能は**単一の NDMF パス**（`Expand WNAE parameters`）でまとめて処理されます。型ごとにパスを分けると `VRCExpressionParameters` が 2 回クローンされてしまうため、意図的に 1 つに束ねています。

実行タイミングは NDMF の `Transforming` フェーズ、`AfterPlugin("nadena.dev.modular-avatar")` 指定により **Modular Avatar のパラメータリネームとメニュー結合が完了した後**です。

処理順は次の通りです。

0. **Parameter Calculation を展開**（全コントローラ）
   - CmpInt の値域検出より前に行うことで、演算結果の書き込みを「値域を静的に決められない Copy」として検出できます
1. `WNAE CmpInt Settings` / `WNAE CmpFloat Settings` を収集
2. それぞれ値域・継承を解決し、検証結果を Console に出力
3. パラメータ名と生成 Bool 名の重複・衝突を**両方の型をまたいで**検査
4. `VRCExpressionParameters` を **1 回だけ**クローンし、両方の結果を書き込む
   - 元のアセットは複製されるため、プロジェクト内のアセットは変更されません
   - 展開後の合計が 256 bit を超える場合はエラー
5. 元パラメータと生成 Bool 群を**全プレイアブルレイヤーのコントローラ**（Base / Additive / Gesture / Action / FX / Sitting / TPose / IKPose）に宣言
   - これにより、FX で圧縮した結果の Bool を他のレイヤーからも同じ名前で参照できます
6. 変換レイヤーを **FX にのみ**生成（FX が無い場合は新規作成）
7. `WNAE CmpInt Settings` / `WNAE CmpFloat Settings` を削除（`WNAE Parameter Calculation` は 0. の時点で取り除かれます）

### 生成されるレイヤーの共通仕様

- `Has Exit Time = OFF` / `Duration = 0` / `Can Transition To Self = OFF`
  - `Can Transition To Self` を OFF にしているのは、Driver が State 進入時にのみ実行されるためです。ON だと毎フレーム再発火します
- State のモーションは空クリップ（`WNAE Empty`）で、何もアニメートしません
- `Write Defaults` は対象コントローラの既存 State の多数派に自動的に合わせます（混在による VRChat の警告を避けるため）
- `IsLocal` は VRChat 組み込みパラメータで、FX に無い場合のみ追加されます。VRCExpressionParameters には追加しません

---

## 他ツールとの関係

| ツール | 関係 |
|---|---|
| **Modular Avatar** | `AfterPlugin("nadena.dev.modular-avatar")` により MA の後に実行されます。MA Parameters によるリネーム後の名前で処理されるため共存できます |
| **Avatar Optimizer** | `Optimizing` フェーズなので本パスより後に実行されます。影響ありません |
| **VRCExpressionsMenu** | パラメータ名は変わらないため、既存のメニュー（Toggle / Radial / SubMenu）はそのまま動作します。非同期パラメータでもメニューからのローカル操作は可能で、それをエンコードレイヤーが同期します |
| **OSC** | 非同期パラメータも OSC から書き込めるため、OSC 経由の操作もエンコードレイヤー経由で同期されます。ただし CmpInt の**値域の自動検出は OSC を見られない**ため、`Range` の手入力が必要です |

---

## エラーと警告

Inspector には**エラーのみ**表示されます（Parameter Calculation の生成 State 数の表示だけは例外）。警告と情報はビルド時に Console へ出力されます（`[WNAE]` 接頭辞）。

### CmpInt / CmpFloat 共通のエラー（該当エントリは展開されません）

- パラメータ名が空
- パラメータ名が他のエントリと重複している（CmpInt / CmpFloat をまたいで検査）
- 生成される Bool 名が既存パラメータ、または他のエントリと衝突している（`Bool Prefix` で回避）
- 展開後の同期パラメータが 256 bit を超えている

機能ごとの固有エラーは [CmpInt](#cmpint-固有のエラーと警告) / [CmpFloat](#cmpfloat-固有のエラーと警告) / [Parameter Calculation](#parameter-calculation-固有のエラーと警告) を参照してください。

### 情報（Console のみ）

- 解決した値域・ビット数（エントリごとに毎回出力）
- 継承元の Expression Parameter が無いため Default = 0 / Saved = OFF になった
- 展開後の同期パラメータの合計ビット数

---

## 既知の制限・未検証事項

以下は α1 時点のものです。

- **実機（VRChat 上）での動作確認が未了です。** Unity エディタ上での生成結果と、演算ロジックの網羅検証（Bit Width 4 / 8 の全入力組み合わせ）までは確認済みですが、アップロードして複数人で同期させた状態での検証は行っていません
- **`Copy` の Convert Range が Int へ書き込むときの丸め規則が VRChat SDK のソースから確認できません。** そのため除算を含む用途では使わず、結果が厳密な整数になる定数倍（`Mul` / `L-SHIFT`）に限定しています。万一 `Mul` の結果がずれる場合はこの実装が原因です
- **CmpInt の値域自動検出は、OSC や Parameter Calculation の演算結果を追えません。** 該当する場合は `Range` の手入力が必要です
- Div / Mod は Bit Width 8 で 1 個あたり約 1262 State を生成します。多用する場合は Bit Width を絞ってください

---

## ファイル構成

| ファイル | アセンブリ | 内容 |
|---|---|---|
| `WNAE.cs` | Assembly-CSharp | `CmpIntEntry` / `CmpFloatEntry` と値域・ビット計算のユーティリティ |
| `WNAE_CmpIntSettings.cs` | Assembly-CSharp | CmpInt 設定コンポーネント（MonoBehaviour） |
| `WNAE_CmpFloatSettings.cs` | Assembly-CSharp | CmpFloat 設定コンポーネント（MonoBehaviour） |
| `WNAE_ParameterCalculation.cs` | Assembly-CSharp | 演算 Behaviour（StateMachineBehaviour） |
| `Editor/WNAE_Editor.cs` | Assembly-CSharp-Editor | NDMF プラグイン / 単一パス / 共通ヘルパー（検証・カタログ・Animator・ExParams・Inspector 部品） |
| `Editor/WNAE_CmpIntEditor.cs` | Assembly-CSharp-Editor | CmpInt の値域スキャナ / 展開 / Inspector |
| `Editor/WNAE_CmpFloatEditor.cs` | Assembly-CSharp-Editor | CmpFloat の展開 / Inspector |
| `Editor/WNAE_ParameterCalculationEditor.cs` | Assembly-CSharp-Editor | 演算チェーンの展開 / Inspector |

`MonoBehaviour` と `StateMachineBehaviour`（いずれも Unity が MonoScript を必要とする型）は、**クラス名とファイル名を一致させないと Add Component / Add Behaviour の一覧に出てきません**。そのためこれらはそれぞれ専用ファイルに分けています。
