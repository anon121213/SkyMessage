namespace SkyMessage.Client.UI.Kit.Emoji;

/// <summary>A picker category. <see cref="NameKey"/> is a localization key, <see cref="Icon"/> a Lucide icon name.</summary>
public sealed record EmojiCategory(string Key, string NameKey, string Icon, IReadOnlyList<string> Emoji);

/// <summary>
/// Curated standard Unicode emoji (rendered with the platform's emoji font — no image assets), grouped by
/// category. Mostly Unicode ≤ 13 without ZWJ sequences, so they render on Android 8+, iOS and desktop browsers.
/// Singleton, no JS.
/// </summary>
public sealed class EmojiCatalog
{
    public const string RecentKey = "recent";

    public EmojiCatalog()
    {
        Categories =
        [
            Make("smileys", IconNames.Smile, Smileys),
            Make("people", IconNames.Hand, People),
            Make("animals", IconNames.PawPrint, Nature),
            Make("food", IconNames.Coffee, Food),
            Make("travel", IconNames.Plane, Travel),
            Make("activities", IconNames.Trophy, Activities),
            Make("objects", IconNames.Lightbulb, Objects),
            Make("symbols", IconNames.Heart, Symbols),
            Make("flags", IconNames.Flag, Flags),
        ];
        All = Categories.SelectMany(c => c.Emoji).Distinct().ToArray();
    }

    public IReadOnlyList<EmojiCategory> Categories { get; }

    /// <summary>Every emoji in picker order (no duplicates).</summary>
    public IReadOnlyList<string> All { get; }

    /// <summary>Default reaction row for messages (the user's quick reaction is put first by the caller).</summary>
    public IReadOnlyList<string> QuickReactions { get; } = Split("👍 ❤️ 😂 😮 😢 🙏 🔥 👏 🎉 😍");

    /// <summary>Recent emoji shown before any use.</summary>
    public IReadOnlyList<string> DefaultRecent { get; } = Split("👍 ❤️ 😂 😊 🙏 😍 🔥 🎉 😘 👏 😢 🤗");

    private static EmojiCategory Make(string key, string icon, string list) => new(key, $"emoji.category.{key}", icon, Split(list));

    private static string[] Split(string list) => list.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private const string Smileys =
        "😀 😃 😄 😁 😆 😅 🤣 😂 🙂 🙃 😉 😊 😇 🥰 😍 🤩 😘 😗 ☺️ 😚 😙 🥲 😋 😛 😜 🤪 😝 🤑 🤗 🤭 🤫 🤔 " +
        "🤐 🤨 😐 😑 😶 😏 😒 🙄 😬 🤥 😌 😔 😪 🤤 😴 😷 🤒 🤕 🤢 🤮 🤧 🥵 🥶 🥴 😵 🤯 🤠 🥳 🥸 😎 🤓 🧐 " +
        "😕 😟 🙁 ☹️ 😮 😯 😲 😳 🥺 😦 😧 😨 😰 😥 😢 😭 😱 😖 😣 😞 😓 😩 😫 🥱 😤 😡 😠 🤬 😈 👿 💀 " +
        "👻 👽 🤖 💩 🤡 😺 😸 😹 😻 😼 😽 🙀 😿 😾 🙈 🙉 🙊 💋 💌 💘 💝 💖 💗 💓 💞 💕 💟 ❣️ 💔 ❤️ 🧡 💛 " +
        "💚 💙 💜 🤎 🖤 🤍 💯 💢 💥 💫 💦 💨 💬 💭 💤";

    private const string People =
        "👋 🤚 🖐️ ✋ 🖖 👌 🤌 🤏 ✌️ 🤞 🤟 🤘 🤙 👈 👉 👆 👇 ☝️ 👍 👎 ✊ 👊 🤛 🤜 👏 🙌 👐 🤲 🤝 🙏 ✍️ 💅 " +
        "🤳 💪 🦵 🦶 👂 👃 🧠 👀 👁️ 👅 👄 👶 🧒 👦 👧 🧑 👱 👨 🧔 👩 🧓 👴 👵 🙍 🙎 🙅 🙆 💁 🙋 🧏 🙇 🤦 " +
        "🤷 👮 🕵️ 💂 👷 🤴 👸 👳 👲 🧕 🤵 👰 🤰 🤱 👼 🎅 🤶 🦸 🦹 🧙 🧚 🧛 🧜 🧝 🧞 🧟 💆 💇 🚶 🧍 🧎 🏃 " +
        "💃 🕺 👯 🧖 🧗 🏇 ⛷️ 🏂 🏄 🚣 🏊 🚴 🚵 🤸 🤽 🤾 🤹 🧘 🛀 🛌 👭 👫 👬 💏 💑 👪 🗣️ 👤 👥 👣";

    private const string Nature =
        "🐶 🐱 🐭 🐹 🐰 🦊 🐻 🐼 🐨 🐯 🦁 🐮 🐷 🐽 🐸 🐵 🐔 🐧 🐦 🐤 🐣 🐥 🦆 🦅 🦉 🦇 🐺 🐗 🐴 🦄 🐝 🐛 " +
        "🦋 🐌 🐞 🐜 🦗 🕷️ 🐢 🐍 🦎 🦖 🦕 🐙 🦑 🦐 🦞 🦀 🐡 🐠 🐟 🐬 🐳 🐋 🦈 🐊 🐅 🐆 🦓 🦍 🐘 🦛 🦏 🐪 " +
        "🐫 🦒 🦘 🐃 🐂 🐄 🐎 🐖 🐏 🐑 🦙 🐐 🦌 🐕 🐩 🐈 🐓 🦃 🦚 🦜 🦢 🦩 🕊️ 🐇 🦝 🦨 🦡 🦦 🦥 🐁 🐿️ 🦔 " +
        "🐾 🐉 🌵 🎄 🌲 🌳 🌴 🌱 🌿 ☘️ 🍀 🍃 🍂 🍁 🍄 🐚 🌾 💐 🌷 🌹 🥀 🌺 🌸 🌼 🌻 🌞 🌝 🌛 🌜 🌚 🌕 🌙 " +
        "🌎 🌍 🌏 🪐 ⭐ 🌟 ✨ ⚡ ☄️ 🔥 🌪️ 🌈 ☀️ 🌤️ ⛅ 🌥️ ☁️ 🌦️ 🌧️ ⛈️ 🌩️ 🌨️ ❄️ ☃️ ⛄ 🌬️ 💧 ☔ 🌊 🌫️";

    private const string Food =
        "🍏 🍎 🍐 🍊 🍋 🍌 🍉 🍇 🍓 🍈 🍒 🍑 🥭 🍍 🥥 🥝 🍅 🍆 🥑 🥦 🥬 🥒 🌶️ 🌽 🥕 🧄 🧅 🥔 🍠 🥐 🥯 🍞 " +
        "🥖 🥨 🧀 🥚 🍳 🧈 🥞 🧇 🥓 🥩 🍗 🍖 🌭 🍔 🍟 🍕 🥪 🥙 🧆 🌮 🌯 🥗 🥘 🥫 🍝 🍜 🍲 🍛 🍣 🍱 🥟 🍤 " +
        "🍙 🍚 🍘 🍥 🥠 🥮 🍢 🍡 🍧 🍨 🍦 🥧 🧁 🍰 🎂 🍮 🍭 🍬 🍫 🍿 🍩 🍪 🌰 🥜 🍯 🥛 🍼 ☕ 🍵 🧃 🥤 🍶 " +
        "🍺 🍻 🥂 🍷 🥃 🍸 🍹 🧉 🍾 🧊 🥄 🍴 🍽️ 🥣 🥡 🥢 🧂";

    private const string Travel =
        "🚗 🚕 🚙 🚌 🚎 🏎️ 🚓 🚑 🚒 🚐 🚚 🚛 🚜 🛵 🏍️ 🚲 🛴 🚨 🚍 🚘 🚖 🚡 🚠 🚃 🚋 🚝 🚄 🚅 🚂 🚆 🚇 🚊 " +
        "🚉 ✈️ 🛫 🛬 🛩️ 💺 🛰️ 🚀 🛸 🚁 🛶 ⛵ 🚤 🛳️ ⛴️ 🚢 ⚓ ⛽ 🚧 🚦 🚏 🗺️ 🗿 🗽 🗼 🏰 🏯 🏟️ 🎡 🎢 🎠 ⛲ " +
        "⛱️ 🏖️ 🏝️ 🏜️ 🌋 ⛰️ 🏔️ 🗻 🏕️ ⛺ 🏠 🏡 🏘️ 🏚️ 🏗️ 🏭 🏢 🏬 🏣 🏥 🏦 🏨 🏪 🏫 💒 🏛️ ⛪ 🕌 🕍 🛕 ⛩️ 🗾 " +
        "🏞️ 🌅 🌄 🌠 🎇 🎆 🌇 🌆 🏙️ 🌃 🌌 🌉 🌁";

    private const string Activities =
        "⚽ 🏀 🏈 ⚾ 🥎 🎾 🏐 🏉 🥏 🎱 🏓 🏸 🏒 🏑 🥍 🏏 🥅 ⛳ 🪁 🏹 🎣 🤿 🥊 🥋 🎽 🛹 🛷 ⛸️ 🥌 🎿 🎖️ 🏆 " +
        "🥇 🥈 🥉 🏅 🎗️ 🎫 🎟️ 🎪 🎭 🩰 🎨 🎬 🎤 🎧 🎼 🎹 🥁 🎷 🎺 🎸 🪕 🎻 🎲 ♟️ 🎯 🎳 🎮 🧩 🎉 🎊 🎈 🎁 " +
        "🎀 🎃 🧨 🎏 🎐 🎑 🧧";

    private const string Objects =
        "⌚ 📱 📲 💻 ⌨️ 🖥️ 🖨️ 🖱️ 💾 💿 📀 📷 📸 📹 🎥 📽️ 📞 ☎️ 📟 📠 📺 📻 🎙️ ⏰ ⏱️ ⏲️ 🕰️ ⌛ ⏳ 📡 🔋 🔌 " +
        "💡 🔦 🕯️ 🧯 💸 💵 💴 💶 💷 💰 💳 💎 ⚖️ 🧰 🔧 🔨 🛠️ 🔩 ⚙️ 🧱 🧲 🧭 🔮 📿 🧿 💈 🔭 🔬 🩹 🩺 💊 💉 " +
        "🧬 🌡️ 🧹 🧺 🧻 🚿 🛁 🧼 🧽 🧴 🛎️ 🔑 🗝️ 🚪 🪑 🛋️ 🛏️ 🧸 🖼️ 🛍️ 🛒 ✉️ 📩 📨 📧 📥 📤 📦 🏷️ 📪 📫 📬 " +
        "📭 📮 📜 📃 📄 📑 🧾 📊 📈 📉 🗒️ 🗓️ 📆 📅 🗑️ 📇 🗃️ 🗄️ 📋 📁 📂 🗂️ 🗞️ 📰 📓 📔 📒 📕 📗 📘 📙 📚 " +
        "📖 🔖 🧷 🔗 📎 🖇️ 📐 📏 🧮 📌 📍 ✂️ 🖊️ 🖋️ ✒️ 🖌️ 🖍️ 📝 ✏️ 🔍 🔎 🔏 🔐 🔒 🔓";

    private const string Symbols =
        "❤️ 🧡 💛 💚 💙 💜 🤎 🖤 🤍 ✅ ☑️ ✔️ ❌ ❎ ➕ ➖ ➗ ✖️ ♾️ ❓ ❔ ❗ ❕ ‼️ ⁉️ ⚠️ 🚸 ⛔ 🚫 📵 🔞 🆘 " +
        "🆗 🆕 🆒 🆓 🆙 🔝 🔜 🔙 ℹ️ 🔤 🔢 #️⃣ *️⃣ 0️⃣ 1️⃣ 2️⃣ 3️⃣ 4️⃣ 5️⃣ 6️⃣ 7️⃣ 8️⃣ 9️⃣ 🔟 ▶️ ⏸️ ⏯️ ⏹️ ⏺️ ⏭️ ⏮️ ⏩ " +
        "⏪ ◀️ 🔼 🔽 ➡️ ⬅️ ⬆️ ⬇️ ↗️ ↘️ ↙️ ↖️ ↕️ ↔️ ↪️ ↩️ 🔀 🔁 🔂 🔄 🎵 🎶 💲 ™️ ©️ ®️ 〰️ ➰ ➿ 🔘 🔴 🟠 " +
        "🟡 🟢 🔵 🟣 ⚫ ⚪ 🟤 🔺 🔻 🔸 🔹 🔶 🔷 🟥 🟧 🟨 🟩 🟦 🟪 ⬛ ⬜ 🟫 🔈 🔇 🔉 🔊 🔔 🔕 📣 📢 ♻️ ⚜️ 🔱 " +
        "📛 🔰 ⭕ 🛑 ✳️ ❇️ ✴️ 🌀 🌐 💠 ♠️ ♣️ ♥️ ♦️ 🃏 🀄 🕐 🕑 🕒 🕓 🕔 🕕 🕖 🕗 🕘 🕙 🕚 🕛 ☮️ ✝️ ☦️ ☪️ " +
        "✡️ 🕉️ ☯️ ♈ ♉ ♊ ♋ ♌ ♍ ♎ ♏ ♐ ♑ ♒ ♓";

    private const string Flags =
        "🏁 🚩 🏳️ 🏴 🇷🇺 🇹🇭 🇧🇾 🇰🇿 🇺🇦 🇺🇿 🇰🇬 🇹🇯 🇹🇲 🇦🇲 🇦🇿 🇬🇪 🇲🇩 🇱🇻 🇱🇹 🇪🇪 🇫🇮 🇸🇪 🇳🇴 🇩🇰 🇮🇸 🇩🇪 " +
        "🇫🇷 🇮🇹 🇪🇸 🇵🇹 🇬🇧 🇮🇪 🇳🇱 🇧🇪 🇱🇺 🇨🇭 🇦🇹 🇵🇱 🇨🇿 🇸🇰 🇭🇺 🇷🇴 🇧🇬 🇷🇸 🇭🇷 🇸🇮 🇲🇪 🇬🇷 🇨🇾 🇹🇷 🇮🇱 🇦🇪 " +
        "🇸🇦 🇶🇦 🇪🇬 🇲🇦 🇮🇳 🇱🇰 🇲🇻 🇳🇵 🇨🇳 🇭🇰 🇯🇵 🇰🇷 🇲🇳 🇻🇳 🇱🇦 🇰🇭 🇲🇲 🇲🇾 🇸🇬 🇮🇩 🇵🇭 🇦🇺 🇳🇿 🇺🇸 🇨🇦 🇲🇽 " +
        "🇨🇺 🇧🇷 🇦🇷 🇨🇱 🇵🇪 🇨🇴 🇻🇪 🇿🇦 🇰🇪 🇳🇬 🇪🇺 🇺🇳";
}
