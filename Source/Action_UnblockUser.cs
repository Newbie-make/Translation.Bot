// =================================================================================================
//  ADMIN: UNBLOCK USER (!translateunblock) - v12.3 - Fixed Mentions
//  SCRIPT 8/11: Admin Unblock User Logic
// =================================================================================================
//  - [FIX] Fixed double username issue (e.g., "@user, user, text").
//  - [FIX] Ensures clean @[user] start for both Twitch and YouTube.
// =================================================================================================
using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using TranslationBot; // Refers to the namespace defined in the shared library script

public class CPHInline
{
    private BotConfig config;
    private Dictionary<string, Dictionary<string, string>> messageTemplates;
    private string logFilePath;
    public bool Execute()
    {
        // 1. Determine Platform & Moderator Info
        string platform = args.ContainsKey("commandSource") && args["commandSource"].ToString().ToLower() == "youtube" ? "youtube" : "twitch";
        string moderator = args["user"].ToString();
        string moderatorId = args["userId"].ToString();
        // 2. Setup Logging
        logFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, BotConstants.LOG_FILENAME);
        Action<string, string> logger = (s, m) => LogToFile(s, m);
        // 3. Load Configuration
        config = BotHelpers.LoadJsonFile<BotConfig>(BotConstants.CONFIG_FILENAME, logger);
        messageTemplates = BotHelpers.LoadJsonFile<Dictionary<string, Dictionary<string, string>>>(BotConstants.TEMPLATES_FILENAME, logger);
        if (config == null || messageTemplates == null)
            return false;
        // 4. Proactive Logging / Profile Retrieval
        UserProfile modProfile = BotHelpers.GetOrUpdateProfile(moderatorId, moderator, config, logger);
        // 5. Parse Input (Who to unblock?)
        string input = args.ContainsKey("rawInput") ? args["rawInput"].ToString().Trim() : string.Empty;
        // Remove @ prefix if typed
        if (input.StartsWith("@"))
            input = input.Substring(1);
        if (string.IsNullOrWhiteSpace(input))
        {
            SendMessageWithStyle("adminUnblockNoUser", modProfile, platform, moderator);
            return false;
        }

        string userIdToUnblock = null;
        string userNameForMsg = input;
        // 6. User Lookup Strategy
        // Step A: Check local DB (Most reliable if they have chatted before)
        userIdToUnblock = BotHelpers.FindUserIdByUsername(input, logger);
        if (userIdToUnblock == null)
        {
            // Step B: Twitch Fallback (API)
            // If user isn't in local DB but moderator types their Twitch username
            if (platform == "twitch")
            {
                var twitchUser = CPH.TwitchGetUserInfoByLogin(input);
                if (twitchUser != null)
                {
                    userIdToUnblock = twitchUser.UserId;
                    userNameForMsg = twitchUser.UserName;
                }
                // Step C: Try parsing as raw User ID (numeric string)
                else if (long.TryParse(input, out _))
                {
                    userIdToUnblock = input;
                }
            }
            else
            {
                // Step D: YouTube Fallback (Assume input is correct ID/Name)
                userIdToUnblock = input;
            }
        }

        // --- UPDATE CONFIG ---
        // Check if the ID exists in the blacklist dictionary
        if (userIdToUnblock != null && config.UserBlocklist.ContainsKey(userIdToUnblock))
        {
            // Retrieve the stored display name for the confirmation message
            string storedName = config.UserBlocklist[userIdToUnblock];
            if (!string.IsNullOrEmpty(storedName))
                userNameForMsg = storedName;
            // Remove from Blocklist
            config.UserBlocklist.Remove(userIdToUnblock);
            // Save Config
            BotHelpers.SaveConfigFile(config, logger);
            // Success Message: "{0}, user {1} has been unblocked."
            SendMessageWithStyle("adminUnblockConfirm", modProfile, platform, moderator, Quote(userNameForMsg, modProfile));
        }
        else
        {
            // Not Found Message: "{0}, user {1} was not found in the blocklist."
            SendMessageWithStyle("adminUnblockNotFound", modProfile, platform, moderator, Quote(userNameForMsg, modProfile));
        }

        return true;
    }

    // --- HELPER METHODS ---
    private void LogToFile(string status, string message)
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Timestamp: {DateTime.Now:yyyy-MM-dd HH:mm:ss} [UnblockUser] [{status}]");
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

    private string GetBotMessage(UserProfile profile, string baseKey) => BotHelpers.GetBotMessage(profile, baseKey, messageTemplates, new object[0]);
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

    private void SendMessageWithStyle(string baseKey, UserProfile profile, string platform, string user, params object[] args)
    {
        var messageArgs = new Dictionary<string, object>();
        messageArgs["0"] = user; // Passed to template engine, SendAdminMessage handles the prefixing
        for (int i = 0; i < args.Length; i++)
        {
            messageArgs[(i + 1).ToString()] = args[i];
        }

        string messageBody = GetBotMessage(profile, baseKey, messageArgs);
        // Use consistent Admin Message handler
        SendAdminMessage(messageBody, platform, user);
    }
}