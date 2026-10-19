using System.Collections.Concurrent;
using System.Linq;

namespace Ocelot.Modules;

public abstract partial class Module<P, C>
    where P : OcelotPlugin
    where C : IOcelotConfig
{
    // 完整鍵只組一次：這支每幀被面板與設定畫面呼叫很多次，而模組名的蛇形寫法每重算一次就是每個字元一個字串。
    // 呼叫端傳的鍵是常數或每個屬性固定一個，集合有上限；用併行字典是因為模組的 log 也可能在別的執行緒翻譯。
    private ConcurrentDictionary<string, string>? translationKeys;

    private string? translationPrefix;

    public string Translate(string key)
    {
        var prefix = translationPrefix ??= BuildTranslationPrefix(GetType().Name);
        var keys = translationKeys ??= new ConcurrentDictionary<string, string>();
        var fullKey = keys.GetOrAdd(key, static (k, p) => p + k, prefix);

        return I18N.T(fullKey);
    }

    private static string BuildTranslationPrefix(string typeName)
    {
        var module = string.Concat(typeName.Select((x, i) =>
            i > 0 && char.IsUpper(x) ? "_" + x : x.ToString())).ToLower().Replace("_module", "");

        return $"modules.{module}.";
    }

    public string Trans(string key)
    {
        return Translate(key);
    }

    public string T(string key)
    {
        return Translate(key);
    }
}
