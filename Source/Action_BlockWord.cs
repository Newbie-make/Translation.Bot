// =================================================================================================
//  ADMIN: BLOCK WORD (!translateblockword) - v12.1 - Polished Final
//  Admin Block Word Logic
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
        // Dummy logger (file logging not strictly necessary for this simple command, but structure is kept)
        Action<string, string> logger = (s, m) =>
        {
        };
        // 2. Load Configuration
        config = BotHelpers.LoadJsonFile<BotConfig>(BotConstants.CONFIG_FILENAME, logger);
        messageTemplates = BotHelpers.LoadJsonFile<Dictionary<string, Dictionary<string, string>>>(BotConstants.TEMPLATES_FILENAME, logger);
        if (config == null || messageTemplates == null)
            return false;
        // 3. Proactive Logging / Profile Creation
        // Ensure the admin running this command has a profile so we can reply in their preferred language.
        UserProfile modProfile = BotHelpers.GetOrUpdateProfile(moderatorId, moderator, config, logger);
        // 4. Validate Input
        if (string.IsNullOrEmpty(rawInput))
        {
            SendMessageWithStyle("blocklistNoWord", modProfile, platform, moderator);
            return false;
        }

        string word = rawInput;
        // 5. Blocklist Logic
        // Check if word exists (Case Insensitive)
        if (!config.WordBlocklist.Any(w => w.Equals(word, StringComparison.OrdinalIgnoreCase)))
        {
            // Add word to list
            config.WordBlocklist.Add(word);
            // Save to Config.json immediately
            BotHelpers.SaveConfigFile(config, logger);
            // Send Confirmation: "{0}, the word '{1}' has been added..."
            SendMessageWithStyle("blocklistAddConfirm", modProfile, platform, moderator, Quote(word, modProfile));
        }
        else
        {
            // Already Exists: "{0}, the word '{1}' is already blocked."
            SendMessageWithStyle("blocklistAlreadyExists", modProfile, platform, moderator, Quote(word, modProfile));
        }

        return true;
    }

    // --- HELPER METHODS ---
    // 1. Main Wrapper for retrieving localized messages with arguments
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

    // 2. Simple Wrapper for messages without arguments
    private string GetBotMessage(UserProfile profile, string baseKey)
    {
        return BotHelpers.GetBotMessage(profile, baseKey, messageTemplates, new object[0]);
    }

    // 3. Quote Logic
    // Wraps the blocked word in language-specific quotes (e.g. "word" or «word»)
    private string Quote(string text, UserProfile profile)
    {
        string startQuote = GetBotMessage(profile, "quote_start");
        string endQuote = GetBotMessage(profile, "quote_end");
        // Fallback defaults
        if (startQuote.StartsWith("quote_start"))
            startQuote = "'";
        if (endQuote.StartsWith("quote_end"))
            endQuote = "'";
        return $"{startQuote}{text}{endQuote}";
    }

    // 4. Clean Mention Logic
    // Ensures the response follows "@Moderator, Message..." format cleanly
    private void SendAdminMessage(string message, string platform, string moderator)
    {
        // If the template engine inserted the username at the start (e.g. "Moderator, word blocked"), remove it
        if (message.StartsWith(moderator))
        {
            message = message.Substring(moderator.Length);
        }

        // Clean up punctuation (", ", ": ")
        message = message.TrimStart(',', ' ', ':');
        // Prepend @Mention
        string finalMessage = $"@{moderator}, {message}";
        if (platform == "youtube")
            CPH.SendYouTubeMessage(finalMessage);
        else
            CPH.SendMessage(finalMessage);
    }

    // Prepares arguments for the template engine
    private void SendMessageWithStyle(string baseKey, UserProfile profile, string platform, string user, params object[] args)
    {
        var messageArgs = new Dictionary<string, object>();
        messageArgs["0"] = user; // {0} is the user's name
        for (int i = 0; i < args.Length; i++)
        {
            messageArgs[(i + 1).ToString()] = args[i];
        }

        string messageBody = GetBotMessage(profile, baseKey, messageArgs);
        // Pass to SendAdminMessage to handle the prefix formatting
        SendAdminMessage(messageBody, platform, user);
    }
}