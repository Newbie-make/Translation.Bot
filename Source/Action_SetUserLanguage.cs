// =================================================================================================
//  ADMIN: SET USER LANGUAGE (!sul) - v12.1 - Fixed Mentions & Quotes
//  Admin Set User Language Logic
// =================================================================================================
//  - [FIX] Ensures Target User {1} always has an @ mention.
//  - [FIX] Ensures Admin {0} always has an @ mention via SendAdminMessage.
//  - [FIX] Applied strict quote logic from Translation Action.
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
    // Languages that should be lowercase when inserted into a sentence (e.g. "translated to spanish" vs "translated to Spanish")
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
    public bool Execute()
    {
        // 1. Determine Platform & Admin Info
        string platform = args.ContainsKey("commandSource") && args["commandSource"].ToString().ToLower() == "youtube" ? "youtube" : "twitch";
        string adminUser = args.ContainsKey("user") ? args["user"].ToString() : "Admin";
        string adminId = args.ContainsKey("userId") ? args["userId"].ToString() : "";
        // 2. Setup Logging
        logFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, BotConstants.LOG_FILENAME);
        Action<string, string> logger = (s, m) => LogToFile(s, m);
        // 3. Load Configuration
        config = BotHelpers.LoadJsonFile<BotConfig>(BotConstants.CONFIG_FILENAME, logger);
        messageTemplates = BotHelpers.LoadJsonFile<Dictionary<string, Dictionary<string, string>>>(BotConstants.TEMPLATES_FILENAME, logger);
        if (config == null || messageTemplates == null)
            return false;
        // 4. Proactive Logging / Profile Retrieval for Admin
        // We need the admin's profile to know which language to use for the CONFIRMATION message.
        UserProfile adminProfile = BotHelpers.GetOrUpdateProfile(adminId, adminUser, config, logger);
        // 5. Parse Input
        // Input format: "@TargetUser target:es style:pirate"
        string rawInput = args.ContainsKey("rawInput") ? args["rawInput"].ToString().Trim() : string.Empty;
        string[] parts = rawInput.Split(new[] { ' ' }, 2); // Split into [TargetName] [Rest of args]
        if (parts.Length == 0 || string.IsNullOrEmpty(parts[0]))
        {
            SendMessageWithStyle("sulNoUser", adminProfile, platform, adminUser);
            return false;
        }

        string targetInputName = parts[0].StartsWith("@") ? parts[0].Substring(1) : parts[0]; // Strip @
        string commandInput = parts.Length > 1 ? parts[1] : string.Empty;
        // 6. Find Target User ID
        // Step A: Check Local DB
        string targetUserId = BotHelpers.FindUserIdByUsername(targetInputName, logger);
        // Step B: Fallback for Twitch (External API lookup)
        if (targetUserId == null && platform == "twitch")
        {
            var userInfo = CPH.TwitchGetUserInfoByLogin(targetInputName);
            if (userInfo != null)
            {
                targetUserId = userInfo.UserId;
                targetInputName = userInfo.UserName; // Update casing to match Twitch official
            }
        }

        // Step C: YouTube fallback (Trust input as ID/Name)
        if (targetUserId == null && platform == "youtube")
        {
            targetUserId = targetInputName;
        }

        if (targetUserId == null)
        {
            SendMessageWithStyle("sulNoUser", adminProfile, platform, adminUser);
            return false;
        }

        // 7. Load Target Profile
        var allUserProfiles = BotHelpers.LoadUserProfiles(logger);
        UserProfile targetProfile;
        if (!allUserProfiles.TryGetValue(targetUserId, out targetProfile))
        {
            // Create profile for target if they don't exist yet in our system
            targetProfile = BotHelpers.CreateDefaultUserProfile(config, logger);
            targetProfile.Username = targetInputName;
        }

        // 8. Execute Command (Check or Set)
        if (string.IsNullOrEmpty(commandInput))
        {
            // Just "!sul @User" -> Check settings
            HandleCheckCommand(targetProfile, platform, adminUser, targetInputName, adminProfile);
        }
        else
        {
            // "!sul @User target:es" -> Update settings
            HandleSetCommand(targetProfile, allUserProfiles, platform, adminUser, targetUserId, targetInputName, commandInput, adminProfile);
        }

        return true;
    }

    // --- LOGIC: CHECK SETTINGS ---
    private void HandleCheckCommand(UserProfile targetProfile, string platform, string adminUser, string targetUserName, UserProfile adminProfile)
    {
        // Get friendly names in the ADMIN's language (adminProfile)
        string targetName = GetFriendlyName(targetProfile.TargetLanguage, adminProfile);
        string speakingName = GetFriendlyName(targetProfile.SpeakingLanguage, adminProfile);
        string styleName = GetFriendlyName(targetProfile.SpeakingStyle, adminProfile);
        string pronouns = !string.IsNullOrEmpty(targetProfile.Pronouns) ? Quote(targetProfile.Pronouns, adminProfile) : GetFriendlyName("none", adminProfile);
        var messageArgs = new Dictionary<string, object>
        {
            {
                "0",
                adminUser
            }, // Admin Name (Sender)
            {
                "1",
                "@" + targetUserName
            }, // Target Name (Fixed with @)
            {
                "2",
                targetName
            },
            {
                "3",
                speakingName
            },
            {
                "4",
                styleName
            },
            {
                "5",
                pronouns
            }
        };
        string messageBody = GetBotMessage(adminProfile, "sulCheck", messageArgs);
        SendAdminMessage(messageBody, platform, adminUser);
    }

    // --- LOGIC: CLEAR SETTINGS ---
    private void HandleClearCommand(Dictionary<string, UserProfile> allUserProfiles, string platform, string adminUser, string targetUserId, string targetUserName, UserProfile adminProfile)
    {
        var defaultProfile = BotHelpers.CreateDefaultUserProfile(config, (s, m) => LogToFile(s, m));
        defaultProfile.Username = targetUserName; // Maintain username linkage
        allUserProfiles[targetUserId] = defaultProfile;
        BotHelpers.SaveUserProfiles(allUserProfiles, (s, m) => LogToFile(s, m));
        var messageArgs = new Dictionary<string, object>
        {
            {
                "0",
                adminUser
            },
            {
                "1",
                "@" + targetUserName
            }
        };
        string messageBody = GetBotMessage(adminProfile, "sulClearConfirm", messageArgs);
        SendAdminMessage(messageBody, platform, adminUser);
    }

    // --- LOGIC: SET SETTINGS ---
    private void HandleSetCommand(UserProfile targetProfile, Dictionary<string, UserProfile> allUserProfiles, string platform, string adminUser, string targetUserId, string targetUserName, string commandInput, UserProfile adminProfile)
    {
        // Clean input: "target: es" -> "target:es"
        string cleanedInput = Regex.Replace(commandInput, @":\s+", ":");
        string[] parts = cleanedInput.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        // Check for "Clear" command
        if (parts.Length == 1 && BotHelpers.ResolveKeyword(parts[0], adminProfile.SpeakingLanguage, config.SettingMap) == "clear")
        {
            HandleClearCommand(allUserProfiles, platform, adminUser, targetUserId, targetUserName, adminProfile);
            return;
        }

        var changes = new Dictionary<string, string>();
        bool settingsChanged = false;
        var parsedArgs = new Dictionary<string, string>();
        // Parse Arguments
        foreach (var part in parts)
        {
            if (!part.Contains(":"))
                continue;
            string[] kvp = part.Split(new[] { ':' }, 2);
            if (kvp.Length == 2 && !string.IsNullOrEmpty(kvp[1]))
            {
                parsedArgs[kvp[0].ToLower()] = kvp[1];
            }
        }

        // Shorthand Logic: "!sul @User es" -> "target:es"
        if (parts.Length == 1 && !parts[0].Contains(":"))
        {
            string langCode = parts[0].ToLower();
            if (config.LanguageMap.ContainsKey(langCode))
            {
                targetProfile.TargetLanguage = langCode;
                changes["target"] = GetFriendlyName(langCode, adminProfile);
                settingsChanged = true;
            }
        }

        // Apply Arguments
        foreach (var arg in parsedArgs)
        {
            // Resolve keywords using Admin's language (Admin is typing the command)
            string internalKey = BotHelpers.ResolveKeyword(arg.Key, adminProfile.SpeakingLanguage, config.SettingMap);
            if (internalKey == null)
                continue;
            string valueLower = arg.Value.ToLower();
            switch (internalKey)
            {
                case "target":
                    if (config.LanguageMap.ContainsKey(valueLower))
                    {
                        targetProfile.TargetLanguage = valueLower;
                        changes["target"] = GetFriendlyName(valueLower, adminProfile);
                        settingsChanged = true;
                    }

                    break;
                case "speaking":
                    if (messageTemplates.ContainsKey(valueLower))
                    {
                        targetProfile.SpeakingLanguage = valueLower;
                        changes["speaking"] = GetFriendlyName(valueLower, adminProfile);
                        settingsChanged = true;
                    }

                    break;
                case "style":
                    string internalStyle = BotHelpers.ResolveKeyword(valueLower, adminProfile.SpeakingLanguage, config.StyleMap);
                    if (internalStyle != null)
                    {
                        targetProfile.SpeakingStyle = internalStyle;
                        changes["style"] = GetFriendlyName(internalStyle, adminProfile);
                        settingsChanged = true;
                    }

                    break;
                case "pronouns":
                    targetProfile.Pronouns = arg.Value;
                    changes["pronouns"] = arg.Value;
                    settingsChanged = true;
                    break;
            }
        }

        // Save and Confirm
        if (settingsChanged)
        {
            targetProfile.Username = targetUserName;
            try
            {
                targetProfile._UserComment = $"Profile for: {targetUserName}";
            }
            catch
            {
            }

            allUserProfiles[targetUserId] = targetProfile;
            BotHelpers.SaveUserProfiles(allUserProfiles, (s, m) => LogToFile(s, m));
            // Build Confirmation Message
            var confirmationParts = new List<string>();
            if (changes.ContainsKey("target"))
                confirmationParts.Add(GetBotMessage(adminProfile, "confirmPartTarget", new Dictionary<string, object> { { "0", changes["target"] } }));
            if (changes.ContainsKey("speaking"))
                confirmationParts.Add(GetBotMessage(adminProfile, "confirmPartSpeaking", new Dictionary<string, object> { { "0", changes["speaking"] } }));
            if (changes.ContainsKey("style"))
                confirmationParts.Add(GetBotMessage(adminProfile, "confirmPartStyle", new Dictionary<string, object> { { "0", changes["style"] } }));
            if (changes.ContainsKey("pronouns"))
                confirmationParts.Add(GetBotMessage(adminProfile, "confirmPartPronouns", new Dictionary<string, object> { { "0", Quote(changes["pronouns"], adminProfile) } }));
            string confirmationDetails = string.Join(", ", confirmationParts);
            var messageArgs = new Dictionary<string, object>
            {
                {
                    "0",
                    adminUser
                }, // Admin Name
                {
                    "1",
                    "@" + targetUserName
                }, // Target Name
                {
                    "2",
                    confirmationDetails
                } // Details
            };
            string messageBody = GetBotMessage(adminProfile, "sulConfirmMulti", messageArgs);
            SendAdminMessage(messageBody, platform, adminUser);
        }
    }

    // --- HELPER METHODS ---
    private void LogToFile(string status, string message)
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Timestamp: {DateTime.Now:yyyy-MM-dd HH:mm:ss} [SUL Command] [{status}]");
            sb.AppendLine($"Message: {message}");
            sb.AppendLine(new string ('-', 40));
            File.AppendAllText(logFilePath, sb.ToString());
        }
        catch
        {
        }
    }

    private string GetBotMessage(UserProfile profile, string baseKey, Dictionary<string, object> args = null)
    {
        if (args == null || args.Count == 0)
            return BotHelpers.GetBotMessage(profile, baseKey, messageTemplates, new object[0]);
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

    // Overload for Quotes
    private string GetBotMessage(string baseKey, UserProfile profile)
    {
        return BotHelpers.GetBotMessage(profile, baseKey, messageTemplates, new object[0]);
    }

    private string Quote(string text, UserProfile profile)
    {
        string startQuote = GetBotMessage("quote_start", profile);
        string endQuote = GetBotMessage("quote_end", profile);
        if (startQuote.Equals("quote_start", StringComparison.OrdinalIgnoreCase))
            startQuote = "'";
        if (endQuote.Equals("quote_end", StringComparison.OrdinalIgnoreCase))
            endQuote = "'";
        return $"{startQuote}{text}{endQuote}";
    }

    // =========================================================================================
    // FIX APPLIED HERE: Consistent Messaging Wrapper
    // =========================================================================================
    private void SendAdminMessage(string message, string platform, string moderator)
    {
        // 1. If the message generated by the template already starts with the username, strip it out.
        if (message.StartsWith(moderator))
        {
            message = message.Substring(moderator.Length);
        }

        // 2. Clean up any leftover punctuation/spacing at the start
        message = message.TrimStart(',', ' ', ':');
        // 3. Force the @mention at the start
        string finalMessage = $"@{moderator}, {message}";
        if (platform == "youtube")
            CPH.SendYouTubeMessage(finalMessage);
        else
            CPH.SendMessage(finalMessage);
    }

    // Wrapper to prepare template args
    private void SendMessageWithStyle(string baseKey, UserProfile profile, string platform, string user, params object[] args)
    {
        var messageArgs = new Dictionary<string, object>();
        messageArgs["0"] = user; // Passed to template, SendAdminMessage handles the prefixing
        for (int i = 0; i < args.Length; i++)
        {
            messageArgs[(i + 1).ToString()] = args[i];
        }

        string messageBody = GetBotMessage(profile, baseKey, messageArgs);
        SendAdminMessage(messageBody, platform, user);
    }

    // Localizes names (e.g., "es" -> "Spanish")
    private string GetFriendlyName(string codeToLocalize, UserProfile profile)
    {
        string key = $"{codeToLocalize}_normal";
        string localizedName = GetBotMessage(profile, key, new Dictionary<string, object>());
        if (localizedName.StartsWith(key))
            localizedName = codeToLocalize;
        if (LanguagesRequiringLowercase.Contains(profile.SpeakingLanguage))
            localizedName = localizedName.ToLower();
        return localizedName;
    }
}