# SpikeSide

VALORANTのエージェント選択、ショップと武器カタログの確認、試合履歴の分析を行うWindows向けアプリです。

## 使い方

GitHub ReleasesからWindows x64版のZIPをダウンロードして展開し、`SpikeSide.exe`を起動します。Riotアカウントへの接続が必要な機能は、アプリ内のログインから利用できます。

## ビルド

Windows上で.NET 9 SDKを使用します。PowerShellで `./build.ps1` を実行すると、チェック後にZIPを`dist`へ作成します。
