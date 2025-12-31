// =================================================================================================
//  ADMIN: BLOCK USER (!translateblock) - v12.2 - Fixed Mentions
//  Admin Block Logic
// =================================================================================================
//  - [FIX] Fixed double username issue (e.g., "@user, user, text").
//  - [FIX] Ensures clean @[user] start for both Twitch and YouTube.
// =================================================================================================
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.IO;
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
        // 4. Proactive Logging
        // Ensure the Moderator executing the command exists in our local DB so we know their language for the response.
        UserProfile modProfile = BotHelpers.GetOrUpdateProfile(moderatorId, moderator, config, logger);
        // 5. Parse Input (Who to block?)
        string input = args.ContainsKey("rawInput") ? args["rawInput"].ToString().Trim() : string.Empty;
        // Sanitize input: Remove @ symbol if moderator typed "@user"
        if (input.StartsWith("@"))
            input = input.Substring(1);
        string userIdToBlock = null;
        string finalDisplayName = input;
        // Case A: No input provided -> Block the last person who spoke in chat
        if (string.IsNullOrWhiteSpace(input))
        {
            userIdToBlock = CPH.GetGlobalVar<string>("lastChatter.userId", false);
            finalDisplayName = CPH.GetGlobalVar<string>("lastChatter.user", false);
            if (string.IsNullOrEmpty(userIdToBlock))
            {
                // Localization: "You must specify a username to block."
                SendMessageWithStyle("adminBlockNoUser", modProfile, platform, moderator);
                return false;
            }
        }
        else
        {
            // Case B: Username provided -> Lookup User ID
            // Step 1: Check local JSON database first
            userIdToBlock = BotHelpers.FindUserIdByUsername(input, logger);
            if (userIdToBlock == null)
            {
                // Step 2: Not in local DB? Fallback to Twitch API (Twitch only)
                // This allows blocking users who haven't used the bot yet.
                if (platform == "twitch")
                {
                    var twitchUser = CPH.TwitchGetUserInfoByLogin(input);
                    if (twitchUser != null)
                    {
                        userIdToBlock = twitchUser.UserId;
                        finalDisplayName = twitchUser.UserName; // Use correct casing from API
                    }
                }
                else
                {
                    // Step 3: For YouTube, we can't easily look up IDs by name via CPH without a valid previous interaction.
                    // Trust the input IS the ID or Name needed for blocking logic (fallback).
                    userIdToBlock = input;
                }
            }
        }

        // Safety Checks
        if (string.IsNullOrEmpty(userIdToBlock))
            return false;
        if (userIdToBlock == moderatorId)
            return false; // Prevent Mod from blocking themselves accidentally
        // 6. Update Blocklist
        if (!config.UserBlocklist.ContainsKey(userIdToBlock))
        {
            // Add to dictionary
            config.UserBlocklist.Add(userIdToBlock, finalDisplayName);
            // Save to Config.json
            BotHelpers.SaveConfigFile(config, logger);
            // Success Message: "{0}, the user {1} has been blocked..."
            SendMessageWithStyle("adminBlockConfirm", modProfile, platform, moderator, Quote(finalDisplayName, modProfile));
        }
        else
        {
            // Already blocked: "{0}, the user {1} is already on the blocklist."
            SendMessageWithStyle("adminBlockAlreadyExists", modProfile, platform, moderator, Quote(finalDisplayName, modProfile));
        }

        return true;
    }

    // --- HELPER METHODS ---
    private void LogToFile(string status, string message)
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Timestamp: {DateTime.Now:yyyy-MM-dd HH:mm:ss} [BlockUser] [{status}]");
            sb.AppendLine($"Message: {message}");
            sb.AppendLine(new string ('-', 40));
            File.AppendAllText(logFilePath, sb.ToString());
        }
        catch
        {
        }
    }

    // Standard Message Retrieval Wrapper
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
    // Localizes quotes based on language (e.g. " vs «)
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
    // FIX APPLIED HERE: Clean Mention Logic
    // =========================================================================================
    private void SendAdminMessage(string message, string platform, string moderator)
    {
        // Many localization templates start with "{0}, message...". 
        // Since we want to force a clean platform mention (e.g., @User), we remove the 
        // name if it was already inserted by the template to avoid "@User, User, message...".
        // 1. Check if message starts with the raw username
        if (message.StartsWith(moderator))
        {
            message = message.Substring(moderator.Length);
        }

        // 2. Clean up any leftover punctuation/spacing at the start (removes ", " or ": ")
        message = message.TrimStart(',', ' ', ':');
        // 3. Force the @mention at the start
        string finalMessage = $"@{moderator}, {message}";
        if (platform == "youtube")
            CPH.SendYouTubeMessage(finalMessage);
        else
            CPH.SendMessage(finalMessage);
    }

    // Wrapper to prepare arguments for the template
    private void SendMessageWithStyle(string baseKey, UserProfile profile, string platform, string user, params object[] args)
    {
        var messageArgs = new Dictionary<string, object>();
        messageArgs["0"] = user; // This puts the name in for the template engine
        for (int i = 0; i < args.Length; i++)
        {
            messageArgs[(i + 1).ToString()] = args[i];
        }

        string messageBody = GetBotMessage(profile, baseKey, messageArgs);
        // Pass to SendAdminMessage which handles the prefix/mention clean-up
        SendAdminMessage(messageBody, platform, user);
    }
}