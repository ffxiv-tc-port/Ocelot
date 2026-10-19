<!-- ffxiv-tc-port 繁體中文說明開始 -->
# Ocelot(台服 fork)

由 OhKannaDuh 開發的 Dalamud 插件框架函式庫：模組化的主視窗與設定畫面、動作鏈（Chain）、
Prowl 尋路追蹤、跨插件 IPC 包裝與在地化，並經 Pictomancy 畫世界疊加層。

## 台服 fork 的目的

讓 `BOCCHI` 用到的是艦隊加固過的相依函式庫，並修正動作鏈與自動化流程在實際使用中卡住、
靜默失敗或跨執行緒讀取遊戲物件的問題。

## 與上游的差異

- **相依改走艦隊 fork 的子模組**：`ECommons`、`Pictomancy`（`ffxiv_pictomancy-sourpuh`）不再用 NuGet
  原版套件，出貨的是加固過的版本。
- **動作鏈的可靠性**：步驟逾時、擲例外或要求中止時不再回報成功、改走取消收尾，子動作也一併收尾；
  卡住超過十分鐘會中止；日誌說出是哪一步逾時、在等哪一個介面；重試型動作（返回營地等）等這一次
  嘗試跑完才往下走；自動化停止時正在進行的返回營地／上坐騎嘗試也一起停下。
- **模組隔離**：單一模組擲例外不再中斷其餘模組的初始化、事件與卸載。
- **主視窗**：各區段的上下順序可以自訂（`ModuleManager.SetMainOrder`，沒有自訂時與原本相同）。
- **執行緒安全**：Prowl 的重新尋路改在主執行緒做，不再跨幀、跨執行緒讀已釋放的遊戲物件。
- **跨插件呼叫**：vnavmesh／WrathCombo 的 IPC 宣告對齊提供端；移除從未被呼叫的 YesAlready 包裝。
- **多處降低記憶體用量**：主視窗、設定畫面與翻譯不再每幀反射與重組字串；沒有東西要畫時不建立
  繪製清單；取模組與相依插件檢查不再每次配置。

## 誰在用它

目前只有 **`BOCCHI`** 一個插件 repo 以子模組使用；BOCCHI 用到的 `ECommons` 與 Pictomancy 也是經由
本 repo 的巢狀子模組帶入。

---

以下為上游原始 README，內容未經修改：

<!-- ffxiv-tc-port 繁體中文說明結束 -->

# Ocelot

Ocelot is an opinionated Dalamud plugin library.

Nuget Package: https://www.nuget.org/packages/FFXIVOcelot

## Version

| Library Version Range | SDK API Version |
|-----------------------|-----------------|
| 0.x.x                 | API 12          |
| 1.x.x                 | API 13          |

