# xestrel — リポジトリルール

## ユーザー固有データを git 履歴に絶対に残さない

ユーザーから提供されるデータ（アバター名・キャラクター名・実プロジェクトのパス・
Material / Texture / Prefab / AnimatorController の実物・エラーログに含まれるパス断片など）は、
**いかなる形でも git の履歴に入れてはならない**。コミットされるファイルだけでなく、
コミットメッセージの本文・テストコード・コメント・ドキュメントの例も対象。

- ユーザー提供の実ファイルは `temp~/` に置く（.gitignore 済み）。temp~/ 以外に
  コピーしない。末尾のチルダは Unity にインポートさせないための慣習で、これが無いと
  テストプロジェクト（このリポジトリをローカルパッケージとして参照する）に
  取り込まれてしまう。
- バグ報告のパスやログを再現テスト・コミットメッセージに書くときは、必ず汎用名に
  置換する（例: `Assets/Avatars/AvatarA`、`Body.mat`、`Avatar_FX.controller`）。
  構造だけを保ち、固有名詞は残さない。
- **コミット前に必ず確認する**: ステージ内容とメッセージに対して固有名詞
  （アバター名・ユーザー名・実パス）を grep する。会話中に登場した固有名詞は
  すべて検索対象。
- 漏れて入れてしまった場合: 未 push なら履歴を書き換えて除去し、バックアップ ref
  （refs/original）も削除する。push 済みなら直ちにユーザーに報告して指示を仰ぐ。

## テストの走らせ方

この DevContainer には Unity 2022.3.22f1 が入っている（ベースは game-ci の Editor
イメージ、構成は DaerD と同じ）。EditMode テストはコンテナ内で完結する。

```
.devcontainer/unity/run-tests.sh                                        # 全件
.devcontainer/unity/run-tests.sh --filter 'Xestrel.Tests.ModelSwapperTests'   # 絞り込み(完全名)
.devcontainer/unity/run-tests.sh --log                                  # 失敗時に Unity ログの末尾も出す
```

- 毎回 Unity をコールド起動するので、1 回数分かかる。
- 出力はサマリと失敗内容だけ。Unity の生ログは `$XESTREL_UNITY_PROJECT/Logs/tests.log`、
  結果 XML は同ディレクトリの `test-results.xml`。ログを丸ごと読み込まないこと（数万行ある）。
- 終了コード: 0 = 全件成功 / 1 = テスト失敗 / 3 = コンパイルエラー等で結果が出なかった /
  4 = ライセンス未設定。
- テストプロジェクトは `/home/node/unity-testproject`（名前付きボリューム）。このリポジトリを
  `file:/workspace` のローカルパッケージとして参照しているので、リポジトリ側には Library/ も
  Assets/ も生成されない。
- xestrel は VRChat SDK なしではコンパイルできないので、`setup.sh` が vrc-get で
  `com.vrchat.avatars` を自動で入れる。ほかの VPM パッケージ（NDMF / MA など）と組み合わせて
  確かめたいときは `add-vpm.sh <パッケージ名>`。
- 初回だけ Unity Personal ライセンスの有効化が要る: `.devcontainer/unity/activate-license.sh --status`
  で状態を確認できる。未設定なら手順が表示されるが、ブラウザ操作を含むのでユーザーに依頼すること
  （.alf を生成して temp~/ に出し、ユーザーが Web で .ulf を発行して temp~/ に置く →
  `activate-license.sh --install temp~/<file>.ulf` → `setup.sh`）。
