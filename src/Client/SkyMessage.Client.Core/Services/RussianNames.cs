namespace SkyMessage.Client.Core.Services;

/// <summary>
/// Russian names in the genitive case, for "Сегодня день рождения у Анны". Only what it can decline for sure: one
/// word (a given name, a family word like "Мама" or "Бабушка", a surname with a clear ending), or two or three
/// capitalized words that form a name ("Анна Петрова", "Иван Сергеевич", "Тётя Света", "Петрова Анна Сергеевна").
/// Latin-script names stay as they are (Russian does not decline them). Anything else — a note after the name
/// ("Саша работа"), foreign or indeclinable names, emoji, digits — gives null, and the caller words the text so the
/// name needs no case ending.
/// </summary>
public static class RussianNames
{
    private const string Hushing = "гкхжшщч";
    private const string Consonants = "бвгджзйклмнпрстфхцчшщ";

    // Stems that change or lose a vowel (Павел → Павла), and feminine nouns in -ь.
    private static readonly Dictionary<string, string> Irregular = new(StringComparer.Ordinal)
    {
        ["павел"] = "павла",
        ["лев"] = "льва",
        ["пётр"] = "петра",
        ["петр"] = "петра",
        ["отец"] = "отца",
        ["сынок"] = "сынка",
        ["свёкор"] = "свёкра",
        ["свекор"] = "свекра",
        ["мать"] = "матери",
        ["дочь"] = "дочери",
        ["любовь"] = "любови",
        ["свекровь"] = "свекрови",
        ["игорь"] = "игоря",
        ["зять"] = "зятя",
        ["тесть"] = "тестя",
        ["лазарь"] = "лазаря",
    };

    // Women's names that end in a consonant are not declined; "Ким" may be anyone's.
    private static readonly HashSet<string> Undeclined = new(StringComparer.Ordinal)
    {
        "ирэн", "кармен", "элис", "эстер", "рут", "жаклин", "эвелин", "кэтрин", "мэрилин", "лилиан", "вивиан", "джейн",
        "энн", "ким", "мишель",
    };

    // Given names in -ина, so they are not taken for surnames like "Ильина".
    private static readonly HashSet<string> GivenInIna = new(StringComparer.Ordinal)
    {
        "ирина", "марина", "галина", "алина", "полина", "нина", "кристина", "валентина", "екатерина", "карина", "арина",
        "регина", "ангелина", "каролина", "элина", "дина", "зарина", "лина", "альбина", "сабина", "эвелина", "мальвина",
        "розалина", "аделина", "марфина",
    };

    // Given names in -ин, so they are not taken for surnames like "Ильин".
    private static readonly HashSet<string> GivenInIn = new(StringComparer.Ordinal) { "константин", "валентин", "вениамин", "мартин" };

    // "Тётя Света", "Брат Иван": a family word before the name.
    private static readonly HashSet<string> FamilyWords = new(StringComparer.Ordinal)
    {
        "мама", "папа", "тётя", "тетя", "дядя", "бабушка", "баба", "бабуля", "дедушка", "деда", "дед", "дедуля", "брат",
        "сестра", "сестрёнка", "сестренка", "братишка", "крёстная", "крестная", "крёстный", "крестный", "кума", "кум",
    };

    private enum Kind
    {
        Given,
        Family,
        SurnameMale,
        SurnameFemale,
        PatronymicMale,
        PatronymicFemale,
    }

    /// <summary>"Анна" → "Анны", "Иван Петров" → "Ивана Петрова", "John" → "John"; null when unsure.</summary>
    public static string? Genitive(string? name)
    {
        var text = name?.Trim();
        if (string.IsNullOrEmpty(text) || text.Length > 64) return null;
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length is 0 or > 3) return null;
        if (words.All(IsLatinWord)) return string.Join(' ', words);
        if (!words.All(IsCyrillicWord)) return null;

        if (words.Length == 1) return DeclineSingle(words[0]);

        // Several words are a name only when each is capitalized ("Саша работа" is a name and a note).
        if (!words.All(w => char.IsUpper(w[0]))) return null;
        var kinds = words.Select(KindOf).ToArray();
        if (!IsNamePattern(kinds)) return null;
        var declined = new string[words.Length];
        for (var i = 0; i < words.Length; i++)
        {
            if (Decline(words[i], kinds[i]) is not { } word) return null;
            declined[i] = word;
        }
        return string.Join(' ', declined);
    }

    private static string? DeclineSingle(string word)
    {
        var lower = word.ToLowerInvariant();
        // "Петрова", "Ковальская", "Достоевский": a surname on its own. "Ильина" alone could be a given name too.
        if (lower.Length >= 6 && (lower.EndsWith("ова", StringComparison.Ordinal) || lower.EndsWith("ева", StringComparison.Ordinal)
                                  || lower.EndsWith("ёва", StringComparison.Ordinal)))
            return Decline(word, Kind.SurnameFemale);
        if ((lower.EndsWith("ина", StringComparison.Ordinal) || lower.EndsWith("ына", StringComparison.Ordinal)) && !GivenInIna.Contains(lower))
            return null;
        return Decline(word, KindOf(word) is Kind.SurnameMale or Kind.SurnameFemale ? KindOf(word) : Kind.Given);
    }

    /// <summary>Given + surname (either order), given + patronymic (+ surname), surname + given + patronymic, family word + given.</summary>
    private static bool IsNamePattern(Kind[] kinds)
    {
        static bool Name(Kind k) => k is Kind.Given;
        static bool Surname(Kind k) => k is Kind.SurnameMale or Kind.SurnameFemale;
        static bool Patronymic(Kind k) => k is Kind.PatronymicMale or Kind.PatronymicFemale;
        return kinds switch
        {
            [var a, var b] => (Name(a) && Surname(b)) || (Surname(a) && Name(b)) || (Name(a) && Patronymic(b)) || (a == Kind.Family && Name(b)),
            [var a, var b, var c] => (Name(a) && Patronymic(b) && Surname(c)) || (Surname(a) && Name(b) && Patronymic(c)),
            _ => false,
        };
    }

    private static Kind KindOf(string word)
    {
        var w = word.ToLowerInvariant();
        bool Ends(string suffix, int minLength) => w.Length >= minLength && w.EndsWith(suffix, StringComparison.Ordinal);

        if (FamilyWords.Contains(w)) return Kind.Family;
        if (Ends("вич", 5) || Ends("ьич", 5) || Ends("ич", 6)) return Kind.PatronymicMale;
        if (Ends("вна", 6) || Ends("чна", 6)) return Kind.PatronymicFemale;
        if (Ends("ова", 6) || Ends("ева", 6) || Ends("ёва", 6) || Ends("ская", 6) || Ends("цкая", 6)) return Kind.SurnameFemale;
        if ((Ends("ина", 5) || Ends("ына", 5)) && !GivenInIna.Contains(w)) return Kind.SurnameFemale;
        if (Ends("ов", 5) || Ends("ев", 5) || Ends("ёв", 5) || ((Ends("ин", 5) || Ends("ын", 5)) && !GivenInIn.Contains(w))
            || Ends("ский", 6) || Ends("цкий", 6))
            return Kind.SurnameMale;
        return Kind.Given;
    }

    private static string? Decline(string word, Kind kind)
    {
        var w = word.ToLowerInvariant();
        switch (kind)
        {
            case Kind.SurnameFemale:
                // Петрова → Петровой, Ильина → Ильиной, Ковальская → Ковальской.
                return w.EndsWith("ая", StringComparison.Ordinal) ? Replace(word, 2, "ой") : Replace(word, 1, "ой");
            case Kind.SurnameMale:
                // Достоевский → Достоевского; Петров, Ильин → +а.
                return w.EndsWith("ий", StringComparison.Ordinal) ? Replace(word, 2, "ого") : Replace(word, 0, "а");
            case Kind.PatronymicMale:
                return Replace(word, 0, "а");
            case Kind.PatronymicFemale:
                return Replace(word, 1, "ы");
            default:
                return DeclineNoun(word, w);
        }
    }

    /// <summary>Given names and family words.</summary>
    private static string? DeclineNoun(string word, string w)
    {
        if (w.Length < 2) return null;
        if (Irregular.TryGetValue(w, out var irregular)) return MatchCase(word, irregular);
        if (Undeclined.Contains(w)) return null;

        var last = w[^1];
        var before = w[^2];
        switch (last)
        {
            case 'а':
                // Ольга → Ольги, Саша → Саши; Анна → Анны.
                return Replace(word, 1, Hushing.Contains(before) ? "и" : "ы");
            case 'я':
                if (before == 'а') return Replace(word, 2, "ой"); // Любимая → Любимой
                if (before == 'я') return null;
                return Replace(word, 1, "и"); // Таня → Тани, Мария → Марии, Илья → Ильи
            case 'й':
                if (before is 'ы' or 'о') return Replace(word, 2, "ого"); // Любимый → Любимого, Родной → Родного
                if (before == 'и') return Hushing.Contains(w.Length > 2 ? w[^3] : ' ') ? null : Replace(word, 1, "я"); // Юрий → Юрия
                return Replace(word, 1, "я"); // Андрей → Андрея, Николай → Николая
            case 'ь':
                return null;
            default:
                if (!Consonants.Contains(last)) return null; // Ренато, Нелли, Солнышко: left as they are or unsure
                // A vowel that may drop (Марек, Сынишко…) — not sure.
                if (w.EndsWith("ок", StringComparison.Ordinal) || w.EndsWith("ёк", StringComparison.Ordinal)
                    || w.EndsWith("ек", StringComparison.Ordinal) || w.EndsWith("ец", StringComparison.Ordinal))
                    return null;
                return Replace(word, 0, "а"); // Олег → Олега, Брат → Брата
        }
    }

    /// <summary>Cuts <paramref name="cut"/> letters and adds <paramref name="ending"/>, in capitals for a word in capitals.</summary>
    private static string Replace(string word, int cut, string ending)
    {
        var stem = word[..^cut];
        return stem + (IsAllUpper(word) ? ending.ToUpperInvariant() : ending);
    }

    private static string MatchCase(string word, string lower)
    {
        if (IsAllUpper(word)) return lower.ToUpperInvariant();
        return char.IsUpper(word[0]) ? char.ToUpperInvariant(lower[0]) + lower[1..] : lower;
    }

    private static bool IsAllUpper(string word) => word.Length > 1 && word.All(c => !char.IsLetter(c) || char.IsUpper(c));

    private static bool IsCyrillicWord(string word) => word.Length > 0 && word.All(c => c is >= 'а' and <= 'я' or >= 'А' and <= 'Я' or 'ё' or 'Ё');

    private static bool IsLatinWord(string word) =>
        word.Length > 0 && char.IsAsciiLetter(word[0]) && word.All(c => char.IsAsciiLetter(c) || c is '\'' or '-' or '.');
}
