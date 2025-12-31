// =================================================================================================
//  SET LANGUAGE COMMAND (UNIFIED) - v8.3 - Fix Argument Indices
//  User Settings Management (!sl)
// =================================================================================================
using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using TranslationBot; // Refers to the namespace defined in the shared library script

public class CPHInline
{
    // List of languages that require lowercase formatting when displayed in sentences
    private static readonly HashSet<string> LanguagesRequiringLowercase = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "pt",
        "ptpt",
        "es",
        "fr",
        "it",
        "nl",
        "pl",
        "sv",
        "no",
        "fi",
        "da",
        "ru",
        "tr",
        "uk",
        "cs",
        "bg",
        "hu",
        "is",
        "ro"
    };
    private BotConfig config;
    private Dictionary<string, Dictionary<string, string>> messageTemplates;
    private string logFilePath;
    // --- MAIN EXECUTION ---
    public bool Execute()
    {
        // 1. Determine Platform & User Info
        string platform = args.ContainsKey("commandSource") && args["commandSource"].ToString().ToLower() == "youtube" ? "youtube" : "twitch";
        string user = args.ContainsKey("user") ? args["user"].ToString() : "Someone";
        string userId = args.ContainsKey("userId") ? args["userId"].ToString() : "";
        // 2. Setup Logging
        logFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, BotConstants.LOG_FILENAME);
        Action<string, string> logger = (s, m) => LogToFile(s, m);
        // 3. Load Config & Templates
        config = BotHelpers.LoadJsonFile<BotConfig>(BotConstants.CONFIG_FILENAME, logger);
        messageTemplates = BotHelpers.LoadJsonFile<Dictionary<string, Dictionary<string, string>>>(BotConstants.TEMPLATES_FILENAME, logger);
        if (config == null || messageTemplates == null)
        {
            CPH.SendMessage($"@{user}, Critical error: Configuration missing.", true);
            return false;
        }

        if (string.IsNullOrEmpty(userId))
            return false;
        // 4. Load or Create User Profile
        UserProfile userProfile = BotHelpers.GetOrUpdateProfile(userId, user, config, logger);
        var allUserProfiles = BotHelpers.LoadUserProfiles(logger);
        // 5. Check Blocklist
        if (config.UserBlocklist.ContainsKey(userId))
        {
            SendMessageWithStyle("userBlockedSl", userProfile, platform, user);
            return false;
        }

        // 6. Parse Command Input
        string rawInput = args.ContainsKey("rawInput") ? args["rawInput"].ToString().Trim() : string.Empty;
        // Branching logic:
        // If input is empty (e.g., just "!sl"), show current settings.
        // If input exists (e.g., "!sl es"), attempt to update settings.
        if (string.IsNullOrEmpty(rawInput))
        {
            HandleCheckCommand(userProfile, platform, user);
        }
        else
        {
            HandleSetCommand(userProfile, allUserProfiles, platform, user, userId, rawInput);
        }

        return true;
    }

    // --- LOGIC: VIEW SETTINGS ---
    private void HandleCheckCommand(UserProfile userProfile, string platform, string user)
    {
        // Fetch friendly names for current settings (e.g., convert "es" to "Spanish" or "Español")
        string targetName = GetFriendlyName(userProfile.TargetLanguage, userProfile);
        string speakingName = GetFriendlyName(userProfile.SpeakingLanguage, userProfile);
        string styleName = GetFriendlyName(userProfile.SpeakingStyle, userProfile);
        // Handle Pronouns display
        string pronouns = !string.IsNullOrEmpty(userProfile.Pronouns) ? userProfile.Pronouns : GetFriendlyName("none", userProfile);
        string quotedPronouns = !string.IsNullOrEmpty(userProfile.Pronouns) ? Quote(pronouns, userProfile) : pronouns;
        // Construct arguments for the template "setLangCheck"
        // Indices correspond to {0}..{3} in the JSON template
        var messageArgs = new Dictionary<string, object>
        {
            {
                "0",
                targetName
            },
            {
                "1",
                speakingName
            },
            {
                "2",
                styleName
            },
            {
                "3",
                quotedPronouns
            }
        };
        string messageBody = GetBotMessage(userProfile, "setLangCheck", messageArgs);
        string mention = FormatMention(user, platform);
        SendMessage($"{mention}, {messageBody}", platform);
    }

    // --- LOGIC: UPDATE SETTINGS ---
    private void HandleSetCommand(UserProfile userProfile, Dictionary<string, UserProfile> allUserProfiles, string platform, string user, string userId, string rawInput)
    {
        // Normalize input: "style: pirate" -> "style:pirate"
        string cleanedInput = Regex.Replace(rawInput, @":\s+", ":");
        string[] parts = cleanedInput.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        // Check for specific "Clear/Reset" command
        if (parts.Length == 1 && BotHelpers.ResolveKeyword(parts[0], userProfile.SpeakingLanguage, config.SettingMap) == "clear")
        {
            HandleClearCommand(userProfile, allUserProfiles, platform, user, userId);
            return;
        }

        string originalSpeakingLang = userProfile.SpeakingLanguage;
        string processingLang = originalSpeakingLang; // Logic assumes commands are in the user's native language initially
        var parsedArgs = new Dictionary<string, string>();
        // Parse key:value pairs (e.g., "speaking:es")
        foreach (var part in parts)
        {
            if (part.Contains(":"))
            {
                string[] kvp = part.Split(new[] { ':' }, 2);
                if (kvp.Length != 2 || string.IsNullOrEmpty(kvp[1]))
                {
                    SendMessageWithStyle("invalidPair", userProfile, platform, user, Quote(part, userProfile));
                    return;
                }

                parsedArgs[kvp[0].ToLower()] = kvp[1];
            }
        }

        // --- LANGUAGE INFERENCE LOGIC ---
        // If the user types keywords in English, but their profile is set to Spanish,
        // we need to detect that the command itself is in English to parse it correctly.
        if (parsedArgs.Any())
        {
            // Check if keywords match the user's current profile language
            bool allKeywordsValid = parsedArgs.All(kvp => IsKeywordValidForLanguage(kvp.Key, kvp.Value.ToLower(), originalSpeakingLang));
            // If not valid in current language, try to guess the language of the command
            if (!allKeywordsValid)
            {
                var languageScores = new Dictionary<string, int>();
                foreach (string langCode in config.SettingMap.Keys)
                {
                    if (langCode == "en")
                        continue; // Optimization: we handle EN separately or as fallback
                    // Count how many keywords match this language
                    if (parsedArgs.All(kvp => IsKeywordValidForLanguage(kvp.Key, kvp.Value.ToLower(), langCode)))
                        languageScores[langCode] = parsedArgs.Count;
                }

                if (languageScores.Any())
                {
                    int maxScore = languageScores.Values.Max();
                    var topCandidates = languageScores.Where(kvp => kvp.Value == maxScore).Select(kvp => kvp.Key).ToList();
                    if (topCandidates.Count == 1)
                        processingLang = topCandidates.First(); // Found a unique match
                    else if (topCandidates.Count > 1)
                    {
                        // If multiple languages match the keywords, use priority list from config
                        foreach (string priorityLang in config.InferencePriority)
                        {
                            if (topCandidates.Contains(priorityLang))
                            {
                                processingLang = priorityLang;
                                break;
                            }
                        }
                    }
                }
            }
        }

        // Apply changes
        var changes = new Dictionary<string, string>();
        bool settingsChanged = false;
        // If inference detected a language change via command keywords, update profile immediately
        if (processingLang != originalSpeakingLang)
        {
            userProfile.SpeakingLanguage = processingLang;
            changes["speaking"] = GetFriendlyName(processingLang, userProfile);
            settingsChanged = true;
        }

        // Process parsed arguments
        foreach (var arg in parsedArgs)
        {
            // Translate the command keyword (e.g., "idioma") to internal key (e.g., "speaking")
            string internalKey = BotHelpers.ResolveKeyword(arg.Key, processingLang, config.SettingMap);
            if (internalKey == null)
            {
                SendMessageWithStyle("invalidKey", userProfile, platform, user, Quote(arg.Key, userProfile));
                continue;
            }

            string valueLower = arg.Value.ToLower();
            switch (internalKey)
            {
                case "target":
                    if (config.LanguageMap.ContainsKey(valueLower))
                    {
                        userProfile.TargetLanguage = valueLower;
                        changes["target"] = GetFriendlyName(valueLower, userProfile);
                        settingsChanged = true;
                    }
                    else
                        SendMessageWithStyle("invalidValue", userProfile, platform, user, Quote(arg.Value, userProfile), Quote(arg.Key, userProfile));
                    break;
                case "speaking":
                    if (messageTemplates.ContainsKey(valueLower))
                    {
                        userProfile.SpeakingLanguage = valueLower;
                        changes["speaking"] = GetFriendlyName(valueLower, userProfile);
                        settingsChanged = true;
                    }
                    else
                        SendMessageWithStyle("invalidValue", userProfile, platform, user, Quote(arg.Value, userProfile), Quote(arg.Key, userProfile));
                    break;
                case "style":
                    string internalStyle = BotHelpers.ResolveKeyword(valueLower, processingLang, config.StyleMap);
                    if (internalStyle != null)
                    {
                        userProfile.SpeakingStyle = internalStyle;
                        changes["style"] = GetFriendlyName(internalStyle, userProfile);
                        settingsChanged = true;
                    }
                    else
                        SendMessageWithStyle("invalidValue", userProfile, platform, user, Quote(arg.Value, userProfile), Quote(arg.Key, userProfile));
                    break;
                case "pronouns":
                    // Pronouns are taken as raw string input
                    userProfile.Pronouns = arg.Value;
                    changes["pronouns"] = arg.Value;
                    settingsChanged = true;
                    break;
            }
        }

        // --- SHORTHAND LOGIC ---
        // If user typed just "!sl es", assume they mean "target:es"
        if (parts.Length == 1 && !parts[0].Contains(":"))
        {
            string langCode = parts[0].ToLower();
            if (config.LanguageMap.ContainsKey(langCode))
            {
                userProfile.TargetLanguage = langCode;
                changes["target"] = GetFriendlyName(langCode, userProfile);
                settingsChanged = true;
            }
            else
            {
                SendMessageWithStyle("invalidCode", userProfile, platform, user, Quote(parts[0], userProfile));
                return;
            }
        }

        // Save and Confirm
        if (settingsChanged)
        {
            userProfile._UserComment = $"Profile for: {user}";
            allUserProfiles[userId] = userProfile;
            BotHelpers.SaveUserProfiles(allUserProfiles, (s, m) => LogToFile(s, m));
            // Build Confirmation Message
            var confirmationParts = new List<string>();
            if (changes.ContainsKey("speaking"))
                confirmationParts.Add(GetBotMessage(userProfile, "confirmPartSpeaking", new Dictionary<string, object> { { "0", changes["speaking"] } }));
            if (changes.ContainsKey("target"))
                confirmationParts.Add(GetBotMessage(userProfile, "confirmPartTarget", new Dictionary<string, object> { { "0", changes["target"] } }));
            if (changes.ContainsKey("style"))
                confirmationParts.Add(GetBotMessage(userProfile, "confirmPartStyle", new Dictionary<string, object> { { "0", changes["style"] } }));
            if (changes.ContainsKey("pronouns"))
                confirmationParts.Add(GetBotMessage(userProfile, "confirmPartPronouns", new Dictionary<string, object> { { "0", Quote(changes["pronouns"], userProfile) } }));
            string confirmationDetails = string.Join(", ", confirmationParts);
            // [FIX] Explicitly map details to {0} in the final message template
            var msgArgs = new Dictionary<string, object>
            {
                {
                    "0",
                    confirmationDetails
                }
            };
            string messageBody = GetBotMessage(userProfile, "setLangConfirmMulti", msgArgs);
            string mention = FormatMention(user, platform);
            SendMessage($"{mention}, {messageBody}", platform);
        }
    }

    // --- LOGIC: CLEAR PROFILE ---
    private void HandleClearCommand(UserProfile userProfile, Dictionary<string, UserProfile> allUserProfiles, string platform, string user, string userId)
    {
        var defaultProfile = BotHelpers.CreateDefaultUserProfile(config, (s, m) => LogToFile(s, m));
        defaultProfile.Username = user; // Preserve username for logs
        // Check if anything is actually different from default
        bool hasCustomSettings = userProfile.TargetLanguage != defaultProfile.TargetLanguage || userProfile.SpeakingLanguage != defaultProfile.SpeakingLanguage || userProfile.SpeakingStyle != defaultProfile.SpeakingStyle || !string.IsNullOrEmpty(userProfile.Pronouns);
        if (hasCustomSettings)
        {
            allUserProfiles[userId] = defaultProfile;
            BotHelpers.SaveUserProfiles(allUserProfiles, (s, m) => LogToFile(s, m));
            SendMessageWithStyle("clearConfirm", defaultProfile, platform, user);
        }
        else
        {
            SendMessageWithStyle("clearNone", userProfile, platform, user);
        }
    }

    // --- HELPER METHODS ---
    // Validates if a command keyword (key) and its option (value) exist in the config for a specific language
    private bool IsKeywordValidForLanguage(string key, string value, string lang)
    {
        if (!config.SettingMap.TryGetValue(lang, out var settingSubMap) || !settingSubMap.ContainsKey(key))
            return false;
        string internalKey = settingSubMap[key];
        // If the key is for style, check if the style value is valid in that language
        if (internalKey == "style")
        {
            if (!config.StyleMap.TryGetValue(lang, out var styleSubMap) || !styleSubMap.ContainsKey(value))
                return false;
        }

        return true;
    }

    // Retrieves localized string with index-based replacement ({0}, {1}...)
    private string GetBotMessage(UserProfile profile, string baseKey, Dictionary<string, object> args = null)
    {
        if (args == null || args.Count == 0)
            return BotHelpers.GetBotMessage(profile, baseKey, messageTemplates, new object[0]);
        // Determine the highest index needed (e.g., if we need {2}, size must be 3)
        int maxIndex = -1;
        foreach (var key in args.Keys)
        {
            if (int.TryParse(key, out int index))
            {
                if (index > maxIndex)
                    maxIndex = index;
            }
        }

        if (maxIndex == -1)
            return BotHelpers.GetBotMessage(profile, baseKey, messageTemplates, new object[0]);
        object[] positionalArgs = new object[maxIndex + 1];
        for (int i = 0; i <= maxIndex; i++)
        {
            string key = i.ToString();
            positionalArgs[i] = args.ContainsKey(key) ? args[key] : "";
        }

        return BotHelpers.GetBotMessage(profile, baseKey, messageTemplates, positionalArgs);
    }

    private void LogToFile(string status, string message)
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Timestamp: {DateTime.Now:yyyy-MM-dd HH:mm:ss} [!sl Command] [{status}]");
            sb.AppendLine($"Message: {message}");
            sb.AppendLine(new string ('-', 40));
            File.AppendAllText(logFilePath, sb.ToString());
        }
        catch (Exception ex)
        {
            CPH.LogError($"[Translation.Bot] CRITICAL FAILURE TO WRITE TO LOG FILE: {ex.Message}");
        }
    }

    private void SendMessage(string message, string platform)
    {
        if (platform == "youtube")
            CPH.SendYouTubeMessage(message);
        else
            CPH.SendMessage(message);
    }

    private string FormatMention(string user, string platform)
    {
        return $"@{user}";
    }

    // Wraps text in quotes defined in the language template (e.g., "text" vs «text»)
    private string Quote(string text, UserProfile profile)
    {
        string startQuote = GetBotMessage(profile, "quote_start", new Dictionary<string, object>());
        string endQuote = GetBotMessage(profile, "quote_end", new Dictionary<string, object>());
        // Fallback if key returns itself
        if (startQuote.StartsWith("quote_start"))
            startQuote = "'";
        if (endQuote.StartsWith("quote_end"))
            endQuote = "'";
        return $"{startQuote}{text}{endQuote}";
    }

    // Converts ISO code to readable name (e.g. "es" -> "Spanish")
    private string GetFriendlyName(string codeToLocalize, UserProfile profile)
    {
        string key = $"{codeToLocalize}_normal";
        string localizedName = GetBotMessage(profile, key, new Dictionary<string, object>());
        if (localizedName.StartsWith(key))
            localizedName = codeToLocalize; // Fallback to code if name missing
        // Lowercase if language grammar requires it (e.g., inside a sentence)
        if (LanguagesRequiringLowercase.Contains(profile.SpeakingLanguage))
            localizedName = localizedName.ToLower();
        return localizedName;
    }

    // Helper to format and send a message with arguments, specifically handling user mentions
    private void SendMessageWithStyle(string baseKey, UserProfile profile, string platform, string user, params object[] args)
    {
        var messageArgs = new Dictionary<string, object>();
        messageArgs["0"] = user; // {0} is always the User Name in standard templates
        for (int i = 0; i < args.Length; i++)
        {
            messageArgs[(i + 1).ToString()] = args[i];
        }

        string messageBody = GetBotMessage(profile, baseKey, messageArgs);
        // Ensure mention for YouTube
        if (platform == "youtube")
            CPH.SendYouTubeMessage($"@{user}, {messageBody}");
        else
            CPH.SendMessage(messageBody);
    }
}