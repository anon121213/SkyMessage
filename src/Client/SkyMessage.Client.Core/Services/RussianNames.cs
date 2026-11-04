namespace SkyMessage.Client.Core.Services;

/// <summary>
/// Russian names in the genitive case, for "Сегодня день рождения у Анны". Only what it can decline for sure: one
/// word (a given name, a family word like "Мама" or "Бабушка", a surname with a clear ending), or two or three
/// capitalized words that form a name ("Анна Петрова", "Иван Сергеевич", "Тётя Света", "Петрова Анна Сергеевна").
/// Names Russian leaves as they are come back unchanged: Latin-script ones ("John"), ones ending in a vowel other
/// than а/я ("Нелли", "Ренато", "Шевченко"), surnames in -ых/-их ("Седых") and a woman's name ending in a consonant
/// ("Мариам", "Гульнур Ахметова"). A word ending in a consonant is declined only when it is a man's name or a word
/// like "Брат", or the rest of the name says it is a man's ("Ашот Петров") — "Ковальчук" or "Петросян" on their own
/// may be a woman's. Anything else — a note after the name ("Саша работа"), emoji, digits, a neuter word
/// ("Солнышко"), an adjective whose ending depends on the stress ("Старшая") — gives null, and the caller words the
/// text so the name needs no case ending.
/// </summary>
public static class RussianNames
{
    /// <summary>After these letters -а turns into -и, not -ы (Ольги, Саши).</summary>
    private const string Hushing = "гкхжшщч";
    /// <summary>After these the adjective ending -ая depends on the stress (Большой, Старшей).</summary>
    private const string Sibilants = "жшщч";
    private const string HardConsonants = "бвгджзклмнпрстфхцчшщ";

    private enum Gender
    {
        Unknown,
        Male,
        Female,
    }

    private enum Kind
    {
        Given,
        Family,
        SurnameMale,
        SurnameFemale,
        /// <summary>The same for men and women and never declined: Седых, Долгих, Шевченко.</summary>
        SurnameFixed,
        PatronymicMale,
        PatronymicFemale,
    }

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

    // Men's names and words for men that decline on their own, without a surname to tell whom they are about (Олег →
    // Олега, Шамиль → Шамиля, Георгий → Георгия). Other words that end in a consonant may be a woman's (Ковальчук).
    private static readonly HashSet<string> MensNames = new(StringComparer.Ordinal)
    {
        "абрам", "адам", "азат", "айдар", "алан", "александр", "альберт", "амир", "анвар", "антон", "армен", "арсен", "артём",
        "артем", "артур", "ахмед", "ашот", "богдан", "борис", "булат", "вадим", "вазген", "валентин", "вахтанг", "вениамин",
        "виктор", "влад", "владимир", "владислав", "вован", "всеволод", "вячеслав", "гамлет", "генрих", "герман", "глеб",
        "давид", "дамир", "демьян", "денис", "димон", "егор", "ефим", "захар", "иван", "игнат", "ильдар", "ильнур", "ильяс",
        "иосиф", "ислам", "камил", "карен", "кирилл", "клим", "константин", "леон", "леонид", "лукьян", "макар", "макс",
        "максим", "марат", "марк", "мартин", "мирон", "михаил", "мурат", "назар", "наум", "нестор", "нурлан", "олег", "оскар",
        "остап", "платон", "прохор", "ренат", "ринат", "роберт", "родион", "роман", "ростислав", "руслан", "рустам",
        "самвел", "святослав", "семён", "семен", "серафим", "станислав", "стас", "степан", "тагир", "тамерлан", "тарас",
        "тигран", "тимур", "тихон", "толян", "трофим", "феликс", "филипп", "фёдор", "федор", "эдгар", "эдуард", "эльдар",
        "эрик", "юлиан", "яков", "ян", "ярослав", "шамиль", "эмиль", "марсель", "рамиль", "равиль", "камиль", "георгий",
        // Words for men that people save contacts under ("у Соседа", "у Шефа").
        "сосед", "шеф", "босс", "начальник", "директор", "доктор", "врач", "тренер", "мастер", "друг", "братан", "малыш",
        "учитель", "водитель",
    };

    // Women's names that end in a consonant: never declined (у Мариам). "Ким" or "Мишель" may be anyone's.
    private static readonly HashSet<string> WomensNames = new(StringComparer.Ordinal)
    {
        "ирэн", "кармен", "элис", "эстер", "рут", "жаклин", "эвелин", "кэтрин", "мэрилин", "лилиан", "вивиан", "джейн", "энн",
        "мариам", "марьям", "айгерим", "гульнур",
    };

    // Given names in -ина, so they are not taken for surnames like "Ильина".
    private static readonly HashSet<string> GivenInIna = new(StringComparer.Ordinal)
    {
        "ирина", "марина", "галина", "алина", "полина", "нина", "кристина", "валентина", "екатерина", "карина", "арина",
        "регина", "ангелина", "каролина", "элина", "дина", "зарина", "лина", "альбина", "сабина", "эвелина", "мальвина",
        "розалина", "аделина", "марфина", "мадина", "амина", "руфина",
    };

    // Given names in -ин, so they are not taken for surnames like "Ильин".
    private static readonly HashSet<string> GivenInIn = new(StringComparer.Ordinal) { "константин", "валентин", "вениамин", "мартин" };

    // "Тётя Света", "Брат Иван": a family word before the name, and whom it is about.
    private static readonly Dictionary<string, Gender> FamilyWords = new(StringComparer.Ordinal)
    {
        ["мама"] = Gender.Female, ["мамочка"] = Gender.Female, ["мамуля"] = Gender.Female, ["папа"] = Gender.Male,
        ["папочка"] = Gender.Male, ["папуля"] = Gender.Male, ["батя"] = Gender.Male, ["тётя"] = Gender.Female,
        ["тетя"] = Gender.Female, ["дядя"] = Gender.Male, ["бабушка"] = Gender.Female, ["баба"] = Gender.Female,
        ["бабуля"] = Gender.Female, ["дедушка"] = Gender.Male, ["деда"] = Gender.Male, ["дед"] = Gender.Male,
        ["дедуля"] = Gender.Male, ["брат"] = Gender.Male, ["братик"] = Gender.Male, ["братишка"] = Gender.Male,
        ["сестра"] = Gender.Female, ["сестрёнка"] = Gender.Female, ["сестренка"] = Gender.Female,
        ["сестричка"] = Gender.Female, ["крёстная"] = Gender.Female, ["крестная"] = Gender.Female,
        ["крёстный"] = Gender.Male, ["крестный"] = Gender.Male, ["кума"] = Gender.Female, ["кум"] = Gender.Male,
        ["сын"] = Gender.Male, ["сынок"] = Gender.Male, ["дочка"] = Gender.Female, ["доченька"] = Gender.Female,
        ["жена"] = Gender.Female, ["муж"] = Gender.Male, ["внук"] = Gender.Male, ["внучка"] = Gender.Female,
        ["племянник"] = Gender.Male, ["племянница"] = Gender.Female, ["тёща"] = Gender.Female, ["теща"] = Gender.Female,
        ["шурин"] = Gender.Male, ["сват"] = Gender.Male, ["сватья"] = Gender.Female, ["отчим"] = Gender.Male,
        ["мачеха"] = Gender.Female,
    };

    // Adjectives people save contacts under that are too short for the -ая rule (Милая → Милой; Рая → Раи).
    private static readonly HashSet<string> ShortAdjectives = new(StringComparer.Ordinal)
    {
        "милая", "умная", "злая", "седая", "белая", "тихая", "малая", "юная", "новая",
    };

    // Neuter words that decline (Солнышко → Солнышка): too unlike a name to be sure of.
    private static readonly HashSet<string> NeuterWords = new(StringComparer.Ordinal)
    {
        "чудо", "чадо", "золото", "солнце", "сердце", "счастье", "сокровище",
    };

    // Plurals and pronouns, which would come out wrong (у Родители).
    private static readonly HashSet<string> NotNames = new(StringComparer.Ordinal)
    {
        "родители", "соседи", "дети", "внуки", "коллеги", "подруги", "девочки", "девчонки", "мальчики", "ребята", "друзья",
        "братья", "предки", "все", "они", "она", "оно",
    };

    /// <summary>"Анна" → "Анны", "Иван Петров" → "Ивана Петрова", "Нелли" → "Нелли", "John" → "John"; null when unsure.</summary>
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
        // Whom the name is about, from the words that tell (a surname, a patronymic, a family word).
        var gender = Gender.Unknown;
        for (var i = 0; i < words.Length; i++)
        {
            var told = GenderOf(words[i], kinds[i]);
            if (told == Gender.Unknown) continue;
            if (gender != Gender.Unknown && gender != told) return null;
            gender = told;
        }
        var declined = new string[words.Length];
        for (var i = 0; i < words.Length; i++)
        {
            if (Decline(words[i], kinds[i], gender) is not { } word) return null;
            declined[i] = word;
        }
        return string.Join(' ', declined);
    }

    private static string? DeclineSingle(string word)
    {
        var lower = word.ToLowerInvariant();
        var kind = KindOf(word);
        // "Ильина" alone could be a given name too.
        if (kind == Kind.SurnameFemale && (lower.EndsWith("ина", StringComparison.Ordinal) || lower.EndsWith("ына", StringComparison.Ordinal)))
            return null;
        return Decline(word, kind, GenderOf(word, kind));
    }

    /// <summary>Given + surname (either order), given + patronymic (+ surname), surname + given + patronymic, family word + given.</summary>
    private static bool IsNamePattern(Kind[] kinds)
    {
        static bool Name(Kind k) => k is Kind.Given;
        static bool Surname(Kind k) => k is Kind.SurnameMale or Kind.SurnameFemale or Kind.SurnameFixed;
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

        if (FamilyWords.ContainsKey(w)) return Kind.Family;
        // Иванович, Ильич; the spoken Михалыч, Палыч.
        if (Ends("вич", 5) || Ends("ьич", 5) || Ends("ич", 6) || Ends("ыч", 5)) return Kind.PatronymicMale;
        if (Ends("вна", 6) || Ends("чна", 6)) return Kind.PatronymicFemale;
        if (Ends("ова", 6) || Ends("ева", 6) || Ends("ёва", 6) || Ends("ская", 6) || Ends("цкая", 6)) return Kind.SurnameFemale;
        if ((Ends("ина", 5) || Ends("ына", 5)) && !GivenInIna.Contains(w)) return Kind.SurnameFemale;
        if (Ends("ов", 5) || Ends("ев", 5) || Ends("ёв", 5) || ((Ends("ин", 5) || Ends("ын", 5)) && !GivenInIn.Contains(w))
            || Ends("ский", 6) || Ends("цкий", 6))
            return Kind.SurnameMale;
        // Седых, Черных; Долгих, Сухих (not Генрих); Шевченко.
        if (Ends("ых", 5) || Ends("гих", 5) || Ends("ких", 5) || Ends("хих", 5) || Ends("енко", 6)) return Kind.SurnameFixed;
        return Kind.Given;
    }

    private static Gender GenderOf(string word, Kind kind) => kind switch
    {
        Kind.SurnameMale or Kind.PatronymicMale => Gender.Male,
        Kind.SurnameFemale or Kind.PatronymicFemale => Gender.Female,
        Kind.Family => FamilyWords[word.ToLowerInvariant()],
        _ => Gender.Unknown,
    };

    private static string? Decline(string word, Kind kind, Gender gender)
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
            case Kind.SurnameFixed:
                return word;
            case Kind.PatronymicMale:
                return Replace(word, 0, "а");
            case Kind.PatronymicFemale:
                return Replace(word, 1, "ы");
            case Kind.Family:
                return DeclineNoun(word, w, FamilyWords[w]);
            default:
                return DeclineNoun(word, w, gender);
        }
    }

    /// <summary>Given names, family words and the like; <paramref name="gender"/> as the rest of the name tells it.</summary>
    private static string? DeclineNoun(string word, string w, Gender gender)
    {
        if (w.Length < 2) return null;
        if (Irregular.TryGetValue(w, out var irregular)) return MatchCase(word, irregular);
        if (NotNames.Contains(w)) return null;

        var last = w[^1];
        var before = w[^2];
        switch (last)
        {
            case 'а':
                // Франсуа, Ноа: foreign names that stay as they are, or not — unsure.
                if (!HardConsonants.Contains(before)) return null;
                // Ольга → Ольги, Саша → Саши; Анна → Анны.
                return Replace(word, 1, Hushing.Contains(before) ? "и" : "ы");
            case 'я':
                if (before == 'а') return DeclineInAya(word, w);
                if (before == 'я') return null; // Синяя
                return Replace(word, 1, "и"); // Таня → Тани, Мария → Марии, Илья → Ильи, Зоя → Зои
            case 'й':
                if (gender == Gender.Female) return null;
                if (before is 'ы' or 'о') return Replace(word, 2, "ого"); // Любимый → Любимого, Родной → Родного
                // Юрий → Юрия, Георгий → Георгия; Старший, Великий are adjectives (-ого, or -его after the stress).
                if (before == 'и') return Hushing.Contains(w.Length > 2 ? w[^3] : ' ') && !MensNames.Contains(w) ? null : Replace(word, 1, "я");
                return Replace(word, 1, "я"); // Андрей → Андрея, Николай → Николая
            case 'ь':
                // Шамиль, Учитель; a woman's name in -ь (Адель) is declined by some and not by others.
                return gender != Gender.Female && (gender == Gender.Male || MensNames.Contains(w)) ? Replace(word, 1, "я") : null;
            case 'о':
                // Солнышко, Сердечко, Золотко, Чудо decline; Ренато, Бойко, Шевченко do not.
                if (NeuterWords.Contains(w) || w.EndsWith("шко", StringComparison.Ordinal) || w.EndsWith("чко", StringComparison.Ordinal)
                    || w.EndsWith("тко", StringComparison.Ordinal))
                    return null;
                return word;
            case 'е':
                // Счастье, Солнце, Сокровище, Родное, Близкие decline; Рене does not.
                return NeuterWords.Contains(w) || "ьцщоеиы".Contains(before) ? null : word;
            case 'и' or 'у' or 'ю' or 'э' or 'ё':
                return word; // Нелли, Мэри, Ву: not declined
            default:
                return HardConsonants.Contains(last) ? DeclineInConsonant(word, w, gender) : null; // -ы: a plural
        }
    }

    /// <summary>
    /// -ая: an adjective (Любимая → Любимой, Родная, Крёстная) or a short name (Рая → Раи, Тая, Аглая). After ж, ш, щ, ч
    /// the adjective's ending depends on the stress (Большой, Старшей): unsure.
    /// </summary>
    private static string? DeclineInAya(string word, string w)
    {
        if (w.Length < 3) return null;
        var stem = w[^3];
        if (!HardConsonants.Contains(stem) || Sibilants.Contains(stem)) return null;
        if (FamilyWords.ContainsKey(w) || ShortAdjectives.Contains(w) || w.Length >= 6) return Replace(word, 2, "ой");
        return Replace(word, 1, "и");
    }

    /// <summary>
    /// A word ending in a consonant: a woman's name stays as it is (Мариам, Гульнур Ахметова), a man's takes -а (Олег →
    /// Олега, Ашот Петров → Ашота Петрова). On its own an unknown one may be either (Ковальчук, Петросян): unsure.
    /// </summary>
    private static string? DeclineInConsonant(string word, string w, Gender gender)
    {
        var mans = MensNames.Contains(w) || (w.Length >= 4 && w.EndsWith("ик", StringComparison.Ordinal)); // Котик, Эрик
        if (WomensNames.Contains(w)) return gender == Gender.Male ? null : word;
        if (gender == Gender.Female) return mans ? null : word;
        if (gender != Gender.Male && !mans) return null;
        // A vowel that may drop (Марек, Сынишко…): not sure.
        if (w.EndsWith("ок", StringComparison.Ordinal) || w.EndsWith("ёк", StringComparison.Ordinal)
            || w.EndsWith("ек", StringComparison.Ordinal) || w.EndsWith("ец", StringComparison.Ordinal))
            return null;
        return Replace(word, 0, "а");
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
