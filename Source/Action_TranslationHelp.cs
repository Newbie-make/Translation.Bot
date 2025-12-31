// =================================================================================================
//  HELP COMMAND (!translatehelp) - v12.0 - Database & Gender Integration
//  Help Logic
// =================================================================================================
//  - [NEW] Proactive logging via GetOrUpdateProfile.
//  - [FIX] Correct argument format for gendered responses.
//  - [FIX] Clean mentions.
// =================================================================================================
using System;
using System.IO;
using System.Collections.Generic;
using System.Text;
using TranslationBot; // Refers to the namespace defined in the shared library script

public class CPHInline
{
    private BotConfig config;
    private Dictionary<string, Dictionary<string, string>> messageTemplates;
    private string logFilePath;
    public bool Execute()
    {
        // 1. Determine Platform & User Info
        string platform = args.ContainsKey("commandSource") && args["commandSource"].ToString().ToLower() == "youtube" ? "youtube" : "twitch";
        string user = args.ContainsKey("user") ? args["user"].ToString() : "Someone";
        string userId = args.ContainsKey("userId") ? args["userId"].ToString() : "";
        // 2. Setup Logging
        logFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, BotConstants.LOG_FILENAME);
        Action<string, string> logger = (s, m) => LogToFile(s, m);
        // 3. Load Configuration
        config = BotHelpers.LoadJsonFile<BotConfig>(BotConstants.CONFIG_FILENAME, logger);
        messageTemplates = BotHelpers.LoadJsonFile<Dictionary<string, Dictionary<string, string>>>(BotConstants.TEMPLATES_FILENAME, logger);
        if (config == null || messageTemplates == null)
        {
            CPH.SendMessage($"@{user}, Critical error: Configuration missing.", true);
            return false;
        }

        // 4. Proactive Logging / Profile Retrieval
        // We load the profile here to ensure we know the user's language preference
        // so the help message appears in their native language if possible.
        UserProfile userProfile = BotHelpers.GetOrUpdateProfile(userId, user, config, logger);
        // 5. Determine which Help Link to show
        // Check if user typed specific language code (e.g., "!help es")
        string langCodeArg = args.ContainsKey("rawInput") && args["rawInput"] != null ? args["rawInput"].ToString().Trim().ToLower() : string.Empty;
        // Priority: Argument -> User's Profile Language -> Default
        string linkLookupCode = !string.IsNullOrEmpty(langCodeArg) ? langCodeArg : userProfile.SpeakingLanguage;
        // Try to find the link in the config map
        if (!config.HelpLinks.TryGetValue(linkLookupCode, out string linkToUse))
        {
            // Fallback to English/Default if specific language link isn't found
            config.HelpLinks.TryGetValue("default", out linkToUse);
        }

        // Handle missing link configuration
        if (string.IsNullOrEmpty(linkToUse))
        {
            string message = GetBotMessage(userProfile, "helpLinkNotFound", new Dictionary<string, object>());
            string mention = FormatMention(user, platform);
            SendMessage($"{mention}, {message}", platform);
            return true;
        }

        // 6. Construct Message
        // The 'translateHelp' template expects {0} to be the URL.
        var messageArgs = new Dictionary<string, object>
        {
            {
                "0",
                linkToUse
            }
        };
        // 7. Get Localized Message
        // BotHelpers handles gender logic automatically here based on userProfile.Pronouns
        string helpMessageBody = GetBotMessage(userProfile, "translateHelp", messageArgs);
        // 8. Send Final Message
        string finalMessage = FormatMention(user, platform) + ", " + helpMessageBody;
        SendMessage(finalMessage, platform);
        return true;
    }

    // --- HELPER METHODS ---
    private void LogToFile(string status, string message)
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Timestamp: {DateTime.Now:yyyy-MM-dd HH:mm:ss} [Help Command] [{status}]");
            sb.AppendLine($"Message: {message}");
            sb.AppendLine(new string ('-', 40));
            File.AppendAllText(logFilePath, sb.ToString());
        }
        catch
        {
        }
    }

    // Retrieves localized string with index-based replacement
    private string GetBotMessage(UserProfile profile, string baseKey, Dictionary<string, object> args)
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

    private string FormatMention(string user, string platform)
    {
        return $"@{user}";
    }

    private void SendMessage(string message, string platform)
    {
        if (platform == "youtube")
            CPH.SendYouTubeMessage(message);
        else
            CPH.SendMessage(message);
    }
}