// Strings and preferences shared by the trainer and its setup program.
using System;
using System.Collections.Generic;
using System.Globalization;
using Microsoft.Win32;

static class Preferences
{
    public const string KeyPath = @"Software\" + Product.Id;

    public static string Read(string name)
    {
        using (RegistryKey key = Registry.CurrentUser.OpenSubKey(KeyPath))
            return key == null ? null : key.GetValue(name) as string;
    }

    public static void Write(string name, string value)
    {
        using (RegistryKey key = Registry.CurrentUser.CreateSubKey(KeyPath))
            key.SetValue(name, value);
    }
}

static class Loc
{
    public static readonly string[] Codes = { "en", "ru", "uk", "zh" };
    public static readonly string[] Labels = { "EN", "RU", "UA", "中文" };
    const int English = 0, Chinese = 3;

    public static int Current;
    public static bool IsChinese { get { return Current == Chinese; } }

    // A language the user picked wins, otherwise the Windows display language, otherwise English.
    public static void Initialize()
    {
        int saved = Array.IndexOf(Codes, Preferences.Read("Language"));
        Current = saved >= 0 ? saved : Detect(CultureInfo.CurrentUICulture);
    }

    public static int Detect(CultureInfo culture)
    {
        int index = Array.IndexOf(Codes, culture.TwoLetterISOLanguageName);
        return index >= 0 ? index : English;
    }

    public static string T(string key)
    {
        return Product.Fill(Table[key][Current]);
    }

    public static IEnumerable<string> Keys { get { return Table.Keys; } }

    static readonly Dictionary<string, string[]> Table = new Dictionary<string, string[]>
    {
        { "subtitle", new[] { "Trainer version {version} by dollza", "Трейнер версия {version} by dollza", "Трейнер версія {version} by dollza", "修改器 版本 {version} by dollza" } },
        { "section.survival", new[] { "SURVIVAL", "ВЫЖИВАНИЕ", "ВИЖИВАННЯ", "生存" } },
        { "section.weapons", new[] { "WEAPONS", "ОРУЖИЕ", "ЗБРОЯ", "武器" } },
        { "section.world", new[] { "WORLD", "МИР", "СВІТ", "世界" } },

        { "god", new[] { "God mode", "Бессмертие", "Безсмертя", "无敌模式" } },
        { "god.sub", new[] { "Damage does not get through", "Урон не проходит", "Шкода не проходить", "不受任何伤害" } },
        { "ammo", new[] { "Ammo, grenades, knives", "Патроны, гранаты, ножи", "Набої, гранати, ножі", "弹药、手雷、飞刀" } },
        { "ammo.sub", new[] { "Supply does not decrease", "Запас не уменьшается", "Запас не зменшується", "数量不会减少" } },
        { "money", new[] { "Infinite money", "Бесконечные деньги", "Безкінечні гроші", "无限金钱" } },
        { "money.sub", new[] { "Purchases from traders are free", "Покупки у торговцев бесплатно", "Покупки в торговців безкоштовно", "向商人购买免费" } },
        { "stealth", new[] { "Invisibility", "Невидимость", "Невидимість", "隐身" } },
        { "stealth.sub", new[] { "Enemies do not notice you", "Враги не замечают игрока", "Вороги не помічають гравця", "敌人不会发现你" } },
        { "medkits", new[] { "Infinite medkits", "Бесконечные аптечки", "Безкінечні аптечки", "无限医疗包" } },
        { "medkits.sub", new[] { "Medkits are not used up", "Аптечки не тратятся", "Аптечки не витрачаються", "医疗包不会消耗" } },
        { "filters", new[] { "Infinite filters", "Бесконечные фильтры", "Безкінечні фільтри", "无限滤罐" } },
        { "filters.sub", new[] { "Time and supply are not used up", "Время и запас не тратятся", "Час і запас не витрачаються", "时间和储备不会消耗" } },
        { "nomask", new[] { "No gas mask needed", "Без противогаза", "Без протигазу", "无需防毒面具" } },
        { "nomask.sub", new[] { "Breathe freely on the surface", "Можно дышать на поверхности", "Можна дихати на поверхні", "可在地表自由呼吸" } },
        { "noreload", new[] { "No reload", "Без перезарядки", "Без перезаряджання", "无需换弹" } },
        { "noreload.sub", new[] { "Magazine stays full while firing", "Магазин не пустеет при стрельбе", "Магазин не порожніє під час стрільби", "射击时弹匣不会减少" } },
        { "norecoil", new[] { "No recoil or spread", "Без отдачи и разброса", "Без віддачі й розкиду", "无后坐力和散布" } },
        { "norecoil.sub", new[] { "Bullets go where you aim", "Пули летят точно в прицел", "Кулі летять точно в приціл", "子弹精准命中准星" } },
        { "rapid", new[] { "Rapid fire", "Скорострельность", "Скорострільність", "快速射击" } },
        { "rapid.sub", new[] { "Your weapon fires twice as fast", "Оружие стреляет вдвое быстрее", "Зброя стріляє вдвічі швидше", "武器射速提高一倍" } },
        { "air", new[] { "Air pressure", "Давление воздуха", "Тиск повітря", "气压" } },
        { "air.sub", new[] { "Pneumatic guns and flamethrower", "«Тихарь», «Хельсинг», огнемёт", "Пневматична зброя і вогнемет", "气动武器和火焰喷射器" } },
        { "battery", new[] { "Infinite battery", "Бесконечная батарея", "Безкінечна батарея", "无限电池" } },
        { "battery.sub", new[] { "The flashlight does not run down", "Фонарик не садится", "Ліхтарик не сідає", "手电筒不会没电" } },
        { "freeze", new[] { "Freeze everyone", "Заморозка всех", "Заморозка всіх", "冻结所有人" } },
        { "freeze.sub", new[] { "NPCs and enemies stand still", "NPC и враги стоят на месте", "NPC і вороги стоять на місці", "NPC 和敌人停在原地" } },
        { "speed", new[] { "Fast movement", "Быстрое движение", "Швидкий рух", "快速移动" } },
        { "speed.sub", new[] { "Player speed multiplier", "Множитель скорости игрока", "Множник швидкості гравця", "玩家速度倍率" } },
        { "noclip", new[] { "Noclip flight", "Ноуклип (полёт)", "Ноуклип (політ)", "穿墙飞行" } },
        { "noclip.sub", new[] { "Space up, Ctrl down, through walls", "Пробел вверх, Ctrl вниз, сквозь стены", "Пробіл вгору, Ctrl вниз, крізь стіни", "空格上升，Ctrl 下降，可穿墙" } },

        { "multiplier", new[] { "Multiplier", "Множитель", "Множник", "倍率" } },
        { "sound", new[] { "Sound", "Звук", "Звук", "声音" } },
        { "topmost", new[] { "Always on top", "Поверх окон", "Поверх вікон", "窗口置顶" } },
        { "pos.save", new[] { "Save position", "Сохранить позицию", "Зберегти позицію", "保存位置" } },
        { "pos.save.sub", new[] { "Remembers where you stand", "Запомнить место игрока", "Запам'ятати місце гравця", "记住玩家当前位置" } },
        { "pos.saved", new[] { "Position saved", "Позиция сохранена", "Позицію збережено", "位置已保存" } },
        { "pos.save.button", new[] { "Save", "Сохранить", "Зберегти", "保存" } },
        { "pos.load", new[] { "Teleport to position", "Телепорт к позиции", "Телепорт до позиції", "传送到位置" } },
        { "pos.load.sub", new[] { "Instantly, through walls", "Мгновенно, сквозь стены", "Миттєво, крізь стіни", "瞬间传送，可穿墙" } },
        { "pos.load.none", new[] { "Save a position first", "Сначала сохраните позицию", "Спочатку збережіть позицію", "请先保存位置" } },
        { "pos.load.button", new[] { "Go", "Перенести", "Перенести", "传送" } },
        { "disable", new[] { "Everything off", "Выключить всё", "Вимкнути все", "全部关闭" } },
        { "hint", new[] { "Click a key label to change it. Everything turns off on exit.", "Нажмите на клавишу в строке, чтобы сменить её. При закрытии всё отключается.", "Натисніть на клавішу в рядку, щоб змінити її. Під час закриття все вимикається.", "点击按键标签即可更改。退出后全部恢复。" } },
        { "key.press", new[] { "Press a key", "Нажмите клавишу", "Натисніть клавішу", "请按键" } },
        { "notfound", new[] { "Not found in this game version", "Не найдено в этой версии", "Не знайдено в цій версії", "此版本中未找到" } },

        { "status.notrunning", new[] { "Game is not running", "Игра не запущена", "Гру не запущено", "游戏未运行" } },
        { "status.waiting", new[] { "Waiting for the game", "Жду загрузку игры", "Очікування гри", "正在等待游戏" } },
        { "status.unsupported", new[] { "Unsupported version (Steam only)", "Версия не подходит (только Steam)", "Версія не підходить (лише Steam)", "版本不受支持（仅限 Steam）" } },
        { "status.wronggame", new[] { "The running game is not {game}", "Запущена не {game}", "Запущено не {game}", "正在运行的不是 {game}" } },
        { "status.access", new[] { "No access to the game", "Нет доступа к игре", "Немає доступу до гри", "无法访问游戏进程" } },
        { "status.connected", new[] { "Connected · {0} of {1}", "Подключено · {0} из {1}", "Підключено · {0} з {1}", "已连接 · {0}/{1}" } },
        { "status.lost", new[] { "Connection lost", "Связь потеряна", "Зв'язок втрачено", "连接已断开" } },

        { "setup.window", new[] { "{game} Trainer Setup", "Установка {game} Trainer", "Встановлення {game} Trainer", "{game} 修改器安装程序" } },
        { "setup.subtitle", new[] { "Setup for the current user, no administrator rights needed", "Установка для текущего пользователя, без прав администратора", "Встановлення для поточного користувача, без прав адміністратора", "仅为当前用户安装，无需管理员权限" } },
        { "setup.folder", new[] { "Install folder", "Папка установки", "Тека встановлення", "安装位置" } },
        { "setup.gamefolder", new[] { "Game folder", "Папка игры", "Тека гри", "游戏位置" } },
        { "setup.browse", new[] { "Browse…", "Обзор…", "Огляд…", "浏览…" } },
        { "setup.steamonly", new[] { "Works with the Steam version of the game only", "Работает только со Steam-версией игры", "Працює лише зі Steam-версією гри", "仅支持 Steam 版游戏" } },
        { "setup.pick.install", new[] { "Choose where to install the trainer", "Выберите, куда установить трейнер", "Виберіть, куди встановити трейнер", "请选择修改器的安装位置" } },
        { "setup.pick.game", new[] { "Choose the {game} folder", "Выберите папку {game}", "Виберіть теку {game}", "请选择 {game} 所在文件夹" } },
        { "setup.game.ok", new[] { "Steam version of the game found", "Найдена Steam-версия игры", "Знайдено Steam-версію гри", "已找到 Steam 版游戏" } },
        { "setup.game.missing", new[] { "Game not found. Choose its folder with Browse.", "Игра не найдена. Укажите её папку кнопкой «Обзор».", "Гру не знайдено. Вкажіть її теку кнопкою «Огляд».", "未找到游戏。请点击“浏览”选择游戏文件夹。" } },
        { "setup.game.nofile", new[] { "There is no metro.exe in this folder", "В этой папке нет metro.exe", "У цій теці немає metro.exe", "此文件夹中没有 metro.exe" } },
        { "setup.game.notsteam", new[] { "This is not the Steam version, the trainer will not work with it", "Это не Steam-версия, трейнер с ней работать не будет", "Це не Steam-версія, трейнер з нею не працюватиме", "这不是 Steam 版，修改器无法使用" } },
        { "setup.game.wrong", new[] { "This is a different game, the trainer needs {game}", "Это другая игра, нужна {game}", "Це інша гра, потрібна {game}", "这是其他游戏，需要 {game}" } },
        { "setup.desktop", new[] { "Create a desktop shortcut", "Создать ярлык на рабочем столе", "Створити ярлик на робочому столі", "创建桌面快捷方式" } },
        { "setup.launch", new[] { "Start the trainer after setup", "Запустить трейнер после установки", "Запустити трейнер після встановлення", "安装后启动修改器" } },
        { "setup.install", new[] { "Install", "Установить", "Встановити", "安装" } },
        { "setup.close", new[] { "Close", "Закрыть", "Закрити", "关闭" } },
        { "setup.done", new[] { "Installed", "Установлено", "Встановлено", "安装完成" } },
        { "setup.failed", new[] { "Setup failed: {0}", "Ошибка установки: {0}", "Помилка встановлення: {0}", "安装失败：{0}" } },
        { "setup.busy", new[] { "Close the trainer and try again", "Закройте трейнер и повторите", "Закрийте трейнер і спробуйте ще раз", "请关闭修改器后重试" } },
        { "uninstall.ask", new[] { "Remove {game} Trainer?", "Удалить {game} Trainer?", "Видалити {game} Trainer?", "要删除 {game} 修改器吗？" } },
        { "uninstall.done", new[] { "The trainer has been removed", "Трейнер удалён", "Трейнер видалено", "修改器已删除" } },
        { "uninstall.failed", new[] { "Could not remove the trainer: {0}", "Не удалось удалить трейнер: {0}", "Не вдалося видалити трейнер: {0}", "无法删除修改器：{0}" } },
    };
}
