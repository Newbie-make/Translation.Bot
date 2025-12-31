// =================================================================================================
//  ADMIN: UNBLOCK WORD (!translateunblockword) - v12.1 - Polished Final
//  Admin Unblock Word Logic
// =================================================================================================
//  - [FIX] Words are now properly quoted in the response.
//  - [FIX] Mentions are consistent.
//  - [NEW] Auto-registers Admin in DB.
// =================================================================================================
using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using TranslationBot; // Refers to the namespace defined in the shared library script

public class CPHInline
{
    private BotConfig config;
    private Dictionary<string, Dictionary<string, string>> messageTemplates;
    public bool Execute()
    {
        // 1. Determine Platform & Moderator Info
        string platform = args.ContainsKey("commandSource") && args["commandSource"].ToString().ToLower() == "youtube" ? "youtube" : "twitch";
        string moderator = args["user"].ToString();
        string moderatorId = args["userId"].ToString();
        string rawInput = args.ContainsKey("rawInput") ? args["rawInput"].ToString().Trim() : "";
        // Dummy logger
        Action<string, string> logger = (s, m) =>
        {
        };
        // 2. Load Configuration
        config = BotHelpers.LoadJsonFile<BotConfig>(BotConstants.CONFIG_FILENAME, logger);
        messageTemplates = BotHelpers.LoadJsonFile<Dictionary<string, Dictionary<string, string>>>(BotConstants.TEMPLATES_FILENAME, logger);
        if (config == null || messageTemplates == null)
            return false;
        // 3. Proactive Logging / Profile Retrieval
        // Ensure Admin exists in DB so we know what language to use for the confirmation message.
        UserProfile modProfile = BotHelpers.GetOrUpdateProfile(moderatorId, moderator, config, logger);
        // 4. Validate Input
        if (string.IsNullOrEmpty(rawInput))
        {
            SendMessageWithStyle("blocklistNoWord", modProfile, platform, moderator);
            return false;
        }

        string word = rawInput;
        // 5. Unblock Logic
        // Search for the word in the list (Case Insensitive)
        // We use FirstOrDefault to get the *actual* string stored in the list (e.g. input "BADWORD" finds "badword")
        var match = config.WordBlocklist.FirstOrDefault(w => w.Equals(word, StringComparison.OrdinalIgnoreCase));
        if (match != null)
        {
            // Remove the exact match found
            config.WordBlocklist.Remove(match);
            // Save Configuration
            BotHelpers.SaveConfigFile(config, logger);
            // Success: "{0}, the word '{1}' has been removed from the blocklist."
            SendMessageWithStyle("blocklistRemoveConfirm", modProfile, platform, moderator, Quote(word, modProfile));
        }
        else
        {
            // Not Found: "{0}, the word '{1}' was not found in the blocklist."
            SendMessageWithStyle("blocklistNotFound", modProfile, platform, moderator, Quote(word, modProfile));
        }

        return true;
    }

    // --- HELPER METHODS ---
    // Wrapper for retrieving localized messages with arguments
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

    // Simple wrapper for messages without arguments
    private string GetBotMessage(UserProfile profile, string baseKey)
    {
        return BotHelpers.GetBotMessage(profile, baseKey, messageTemplates, new object[0]);
    }

    // Wraps the word in language-specific quotes (e.g., 'word' vs «word»)
    private string Quote(string text, UserProfile profile)
    {
        string startQuote = GetBotMessage(profile, "quote_start");
        string endQuote = GetBotMessage(profile, "quote_end");
        if (startQuote.StartsWith("quote_start"))
            startQuote = "'";
        if (endQuote.StartsWith("quote_end"))
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
        messageArgs["0"] = user; // {0} is the moderator's name
        for (int i = 0; i < args.Length; i++)
        {
            messageArgs[(i + 1).ToString()] = args[i];
        }

        string messageBody = GetBotMessage(profile, baseKey, messageArgs);
        // Pass to SendAdminMessage which handles the prefixing
        SendAdminMessage(messageBody, platform, user);
    }
}