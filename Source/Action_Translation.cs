// =================================================================================================
//  TRANSLATION BOT (UNIFIED) - v12.3 - Fixed Quotes & Mentions
// =================================================================================================
//  - [FIX] Quote logic now strictly uses JSON values (quote_start/quote_end).
//  - [FIX] Fallback to English quotes only occurs if JSON key is missing.
//  - [FIX] Consistent @Mention logic across platforms.
// =================================================================================================
using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TranslationBot; // Refers to the Namespace defined in other scripts (e.g., BotConfig)

public class CPHInline
{
    // --- DATA STRUCTURES ---
    // Helper class to store pronoun data (e.g., "he/him")
    private class PronounInfo
    {
        public string Pronoun { get; set; }
    }

    // Helper class to break down the user's input message into manageable pieces
    private class TextSegment
    {
        public string TextForApi { get; set; }
        public string Tone { get; set; } = "neutral"; // Default tone
        public List<string> ProperNouns { get; set; } = new List<string>(); // Words wrapped in *stars*
        public Dictionary<string, PronounInfo> ExplicitPronouns { get; set; } = new Dictionary<string, PronounInfo>(); // Words wrapped in %percent%
        public PronounInfo SpeakerPronoun { get; set; } = null; // The user's preferred pronouns
    }

    // --- CONSTANTS ---
    // Google Gemini Model definitions
    private const string GEMINI_PRO_MODEL = "gemini-2.5-pro"; // More expensive, smarter
    private const string GEMINI_FLASH_MODEL = "gemini-2.5-flash"; // Faster, cheaper
    // Platform character limits for chat messages
    private const int TWITCH_CHAR_LIMIT = 500;
    private const int YOUTUBE_CHAR_LIMIT = 200;
    // List of language codes that must be displayed in lowercase in the "Translated to [Language]" header
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
    // Global variables for configuration and templates
    private BotConfig config;
    private Dictionary<string, Dictionary<string, string>> messageTemplates;
    private string logFilePath;
    // --- MAIN EXECUTION METHOD ---
    public bool Execute()
    {
        // 1. Determine Platform (Twitch vs YouTube) based on Streamer.bot arguments
        string platform = args.ContainsKey("commandSource") && args["commandSource"].ToString().ToLower() == "youtube" ? "youtube" : "twitch";
        // 2. Setup Logging
        logFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, BotConstants.LOG_FILENAME);
        Action<string, string> logger = (s, m) => LogToFile(s, m);
        // 3. Extract User Info & Input from arguments
        string user = args["user"].ToString();
        string userId = args["userId"].ToString();
        string rawInput = args.ContainsKey("rawInput") ? args["rawInput"].ToString() : string.Empty;
        string command = args.ContainsKey("command") ? args["command"].ToString() : "!tr";
        UserProfile userProfile = null;
        string modelUsedForTranslation = GEMINI_FLASH_MODEL; // Default to Flash
        string promptForLog = string.Empty;
        // Easter Egg: Check if today is April 1st
        bool isAprilFools = DateTime.Now.Month == 4 && DateTime.Now.Day == 1;
        try
        {
            // 4. Load Configuration Files (Config.json and Templates.json)
            config = BotHelpers.LoadJsonFile<BotConfig>(BotConstants.CONFIG_FILENAME, logger);
            messageTemplates = BotHelpers.LoadJsonFile<Dictionary<string, Dictionary<string, string>>>(BotConstants.TEMPLATES_FILENAME, logger);
            // Safety check: Stop if files fail to load
            if (config == null || messageTemplates == null)
            {
                SendMessage("Bot Admin: Critical error loading config. Translation disabled.", platform);
                return false;
            }

            // 5. User Profile Management (Load existing or create new)
            userProfile = BotHelpers.GetOrUpdateProfile(userId, user, config, logger);
            // 6. Blocklist Check
            if (config.UserBlocklist.ContainsKey(userId))
            {
                string blockedKey = isAprilFools ? "aprilFoolsBlocked" : "userBlocked";
                SendMessageWithStyle(blockedKey, userProfile, platform, user);
                return false;
            }

            // 7. Initial Rate Limit Check (Prevent spamming API)
            // Checks if user has exceeded per-minute or daily limits before processing
            if (!IsRequestAllowed(1, user, false, GEMINI_FLASH_MODEL, out _, out _, userProfile, platform, isAprilFools))
                return false;
            // 8. Help Command Check (If input is empty)
            if (string.IsNullOrWhiteSpace(rawInput))
            {
                string helpLink = config.HelpLinks.ContainsKey("en") ? config.HelpLinks["en"] : "https://translation.bot/help";
                SendMessageWithStyle("helpTranslate", userProfile, platform, user, helpLink);
                return false;
            }

            // 9. Word Blocklist Check
            if (IsInputBlocked(rawInput, user, userProfile, platform, isAprilFools))
                return false;
            // --- TEXT PRE-PROCESSING ---
            string textForProcessing = rawInput;
            const string ESCAPE_MARKER = "__ESCAPED_PRONOUN__";
            // Protect escaped percent signs (e.g., user actually wants to type %)
            textForProcessing = Regex.Replace(textForProcessing, @"\\%", ESCAPE_MARKER);
            // Check if user forced "Pro" model using "!" at end of command (e.g., !tr!)
            bool forceProModel = command.EndsWith("!");
            bool forceFlashModel = false;
            string targetLanguageCode = string.Empty;
            string forcedStyleFromPrefix = null;
            // 10. Command Prefix Parsing (e.g., "es-pirate message")
            if (textForProcessing.StartsWith("\\"))
            {
                // If text starts with backslash, treat everything as raw text (ignore prefixes)
                textForProcessing = textForProcessing.Substring(1).TrimStart();
            }
            else
            {
                // Split first word to check for language/style codes
                string[] parts = textForProcessing.Split(new[] { ' ' }, 2);
                string firstToken = parts[0].ToLower();
                bool prefixConsumed = false;
                // Helper local function to find style keywords
                string ResolveStyle(string token) => BotHelpers.ResolveKeyword(token, userProfile.SpeakingLanguage, config.StyleMap);
                // Handle hyphenated prefixes (e.g., "en-baby")
                if (firstToken.Contains("-"))
                {
                    var subParts = firstToken.Split('-');
                    string foundLang = null;
                    string foundStyle = null;
                    foreach (var p in subParts)
                    {
                        string s = ResolveStyle(p);
                        if (s != null && foundStyle == null)
                        {
                            foundStyle = s;
                            continue;
                        }

                        if (config.LanguageMap.ContainsKey(p) && foundLang == null)
                            foundLang = p;
                    }

                    // Apply found settings and remove prefix from text
                    if (foundLang != null || foundStyle != null)
                    {
                        if (foundLang != null)
                            targetLanguageCode = foundLang;
                        if (foundStyle != null)
                            forcedStyleFromPrefix = foundStyle;
                        if (parts.Length > 1)
                        {
                            textForProcessing = parts[1];
                            prefixConsumed = true;
                        }
                    }
                }

                // Handle simple language code prefix (e.g., "jp message")
                if (!prefixConsumed && parts.Length > 1)
                {
                    if (config.LanguageMap.ContainsKey(firstToken))
                    {
                        targetLanguageCode = firstToken;
                        textForProcessing = parts[1];
                    }
                }
            }

            // --- SEGMENTATION ---
            // Break text into parts based on Tone Tags (e.g., &joking&)
            var segments = new List<TextSegment>();
            var toneParts = Regex.Split(textForProcessing, @"(&[^&]+&)");
            string finalTone = forcedStyleFromPrefix ?? "neutral";
            bool hasToneTag = false;
            // Analyze tags for Model overrides (e.g., &pro&, &flash&)
            var allToneTags = toneParts.Where(p => p.StartsWith("&") && p.EndsWith("&")).Select(p => p.Trim('&').ToLower()).ToList();
            var resolvedModelTags = allToneTags.Select(t => BotHelpers.ResolveKeyword(t, userProfile.SpeakingLanguage, config.ModelMap)).Where(rt => rt != null).ToList();
            if (resolvedModelTags.Contains("pro"))
                forceProModel = true;
            if (resolvedModelTags.Contains("flash"))
                forceFlashModel = true;
            // Analyze tags for Tones/Styles
            var nonModelTags = allToneTags.Where(t => BotHelpers.ResolveKeyword(t, userProfile.SpeakingLanguage, config.ModelMap) == null).ToList();
            var realToneTag = nonModelTags.LastOrDefault(); // Use the last specified tone
            if (realToneTag != null)
            {
                string resolvedTone = BotHelpers.ResolveKeyword(realToneTag, userProfile.SpeakingLanguage, config.ToneMap);
                if (resolvedTone != null)
                {
                    finalTone = resolvedTone;
                    hasToneTag = true;
                }
            }

            // Process individual text segments
            foreach (var part in toneParts)
            {
                if (string.IsNullOrWhiteSpace(part) || (part.StartsWith("&") && part.EndsWith("&")))
                    continue; // Skip empty parts or the tags themselves
                var segment = new TextSegment
                {
                    Tone = finalTone
                };
                string processedText = part.Trim();
                // Extract Proper Nouns (surrounded by *stars*) so we can tell AI not to translate them
                var properNounRegex = new Regex(@"\*([^*]+?)\*");
                foreach (Match match in properNounRegex.Matches(processedText))
                    segment.ProperNouns.Add(match.Groups[1].Value);
                processedText = properNounRegex.Replace(processedText, "$1"); // Remove stars for processing
                // Apply Speaker's Pronouns (from user profile)
                if (!string.IsNullOrEmpty(userProfile.Pronouns))
                    segment.SpeakerPronoun = new PronounInfo
                    {
                        Pronoun = NormalizePronoun(userProfile.Pronouns)
                    };
                // Handle Explicit Pronoun placeholders (e.g., %he/him%)
                // Replaces %pronoun% with [P1], [P2] for the AI prompt
                var pronounRegex = new Regex(@"%([\w\s/-]+)%");
                processedText = pronounRegex.Replace(processedText, match =>
                {
                    string placeholder = $"[P{segment.ExplicitPronouns.Count + 1}]";
                    string pronounInput = match.Groups[1].Value.Trim().ToLower();
                    segment.ExplicitPronouns[placeholder] = new PronounInfo
                    {
                        Pronoun = NormalizePronoun(pronounInput)
                    };
                    return placeholder;
                });
                segment.TextForApi = Regex.Replace(processedText, @"\s+", " ").Trim();
                if (!string.IsNullOrWhiteSpace(segment.TextForApi))
                    segments.Add(segment);
            }

            // If nothing left to process (empty message), just echo it back or exit
            if (!segments.Any())
            {
                SendLongMessage(rawInput, platform);
                return true;
            }

            // --- API CALLS ---
            string apiKey = CPH.GetGlobalVar<string>("geminiApiKey");
            if (string.IsNullOrEmpty(apiKey))
            {
                LogToFile("CRITICAL", "Gemini API Key is not set.");
                SendMessage("Bot Admin: Gemini API Key missing.", platform);
                return false;
            }

            using (var client = new HttpClient())
            {
                // Rate Limit Check (Pre-detection)
                if (!IsRequestAllowed(1, user, true, GEMINI_FLASH_MODEL, out int daily, out int minute, userProfile, platform, isAprilFools))
                    return false;
                // 11. STEP 1: DETECTION - Ask AI "What language is this?"
                string detectionUrl = $"https://generativelanguage.googleapis.com/v1beta/models/{GEMINI_FLASH_MODEL}:generateContent?key={apiKey}";
                string combinedText = string.Join(" ", segments.Select(s => s.TextForApi));
                string detectionPrompt = $"Analyze the following text and respond with ONLY the two-letter ISO 639-1 language code. If unrecognizable, respond with \"und\"... Text: \"{combinedText}\"";
                string detectionResult = PerformApiCall(client, detectionUrl, detectionPrompt, apiKey);
                if (string.IsNullOrEmpty(detectionResult))
                {
                    SendMessageWithStyle(isAprilFools ? "aprilFoolsApiError" : "apiError", userProfile, platform, user);
                    return false;
                }

                string detectedInputCode = SanitizeLanguageCode(detectionResult);
                // 12. Model Logic: Decide whether to use Flash or Pro for translation
                bool isComplex = (segments.Any(s => s.ExplicitPronouns.Any() || s.SpeakerPronoun != null) || hasToneTag || forcedStyleFromPrefix != null);
                if (forceProModel)
                    modelUsedForTranslation = GEMINI_PRO_MODEL;
                else if (forceFlashModel)
                    modelUsedForTranslation = GEMINI_FLASH_MODEL;
                else if (detectedInputCode == "und" || isComplex)
                    modelUsedForTranslation = GEMINI_PRO_MODEL; // Use Pro for gibberish checks or complex styles
                else
                    modelUsedForTranslation = GEMINI_FLASH_MODEL; // Use Flash for simple text
                // 13. Determine Target Language
                string finalTargetCode;
                if (!string.IsNullOrEmpty(targetLanguageCode))
                    finalTargetCode = targetLanguageCode; // User manually specified (e.g. "es message")
                else if (userProfile.TargetLanguage != "default" && detectedInputCode.Equals(userProfile.TargetLanguage, StringComparison.OrdinalIgnoreCase))
                    finalTargetCode = userProfile.SpeakingLanguage; // If input matches their target, swap back to native
                else if (userProfile.TargetLanguage != "default")
                    finalTargetCode = userProfile.TargetLanguage; // Use profile preference
                else
                    // Auto-toggle: If detected as auto-from, go to auto-to. Otherwise go to auto-from.
                    finalTargetCode = detectedInputCode.Equals(config.DefaultSettings.AutoTranslateFrom, StringComparison.OrdinalIgnoreCase) ? config.DefaultSettings.AutoTranslateTo : config.DefaultSettings.AutoTranslateFrom;
                if (!config.LanguageMap.TryGetValue(finalTargetCode, out string targetLanguageName))
                {
                    LogToFile("CONFIG_ERROR", $"Target language code '{finalTargetCode}' missing.");
                    SendMessageWithStyle(isAprilFools ? "aprilFoolsApiError" : "apiError", userProfile, platform, user);
                    return false;
                }

                // Optimization: Don't translate if language matches target (unless style is forced)
                if (detectedInputCode.Equals(finalTargetCode, StringComparison.OrdinalIgnoreCase) && detectedInputCode != "und" && forcedStyleFromPrefix == null)
                {
                    SendMessageWithStyle("alreadyTranslated", userProfile, platform, user);
                    return true;
                }

                // Final Rate Limit Check (Pre-translation)
                if (!IsRequestAllowed(segments.Count, user, true, modelUsedForTranslation, out daily, out minute, userProfile, platform, isAprilFools))
                    return false;
                // 14. STEP 2: TRANSLATION LOOP
                var translatedSegments = new List<string>();
                string translationUrl = $"https://generativelanguage.googleapis.com/v1beta/models/{modelUsedForTranslation}:generateContent?key={apiKey}";
                foreach (var segment in segments)
                {
                    // Create the prompt instructions (System Prompt)
                    promptForLog = CreateTranslationPrompt(segment, user, targetLanguageName, detectedInputCode);
                    // Call API
                    string translatedText = PerformApiCall(client, translationUrl, promptForLog, apiKey);
                    if (string.IsNullOrEmpty(translatedText))
                    {
                        SendMessageWithStyle(isAprilFools ? "aprilFoolsApiError" : "apiError", userProfile, platform, user);
                        return false;
                    }

                    // Handle specific "UNDEF" response (AI detected gibberish)
                    if (translatedText.Trim().Equals("UNDEF", StringComparison.OrdinalIgnoreCase))
                    {
                        SendMessageWithStyle(isAprilFools ? "aprilFoolsUnknownTranslation" : "unknownTranslation", userProfile, platform, user);
                        return true;
                    }

                    // Remove placeholder keys (e.g., [P1]) from final output if API kept them
                    string final = translatedText;
                    foreach (var entry in segment.ExplicitPronouns)
                        final = final.Replace(entry.Key, "");
                    translatedSegments.Add(Regex.Replace(final, @"\s+", " ").Trim());
                }

                // 15. Construct Final Output Message
                string streamerLangCode = "en";
                if (config.DefaultSettings.DefaultBotPersona.Contains("-"))
                    streamerLangCode = config.DefaultSettings.DefaultBotPersona.Split('-')[0];
                var streamerProfile = new UserProfile
                {
                    SpeakingLanguage = streamerLangCode,
                    SpeakingStyle = userProfile.SpeakingStyle
                };
                // Get localized header (e.g., "Translated to Spanish:")
                string localizedTargetName = GetLocalizedLanguageName(finalTargetCode, streamerProfile);
                if (LanguagesRequiringLowercase.Contains(streamerProfile.SpeakingLanguage))
                    localizedTargetName = localizedTargetName.ToLower();
                string headerKey = isAprilFools ? "aprilFoolsHeader" : "translationHeader";
                var headerArgs = new Dictionary<string, object>
                {
                    {
                        "0",
                        $"@{user}"}, // Argument {0}: User Mention
                    {
                        "1",
                        localizedTargetName
                    } // Argument {1}: Language Name
                };
                string header = GetBotMessage(headerKey, streamerProfile, headerArgs);
                string translatedBody = string.Join(" ", translatedSegments);
                // --- QUOTE LOGIC (FIXED) ---
                // Fetches start/end quotes from JSON templates based on language
                string startQuote = GetBotMessage("quote_start", streamerProfile);
                string endQuote = GetBotMessage("quote_end", streamerProfile);
                // Fallback: If the JSON returned the key name because the key was missing, use standard quotes
                if (startQuote.Equals("quote_start", StringComparison.OrdinalIgnoreCase))
                    startQuote = "\"";
                if (endQuote.Equals("quote_end", StringComparison.OrdinalIgnoreCase))
                    endQuote = "\"";
                // Assemble final string
                string finalMessage = $"{header} {startQuote}{translatedBody}{endQuote}";
                // Restore escaped characters
                finalMessage = finalMessage.Replace(ESCAPE_MARKER, "%");
                // 16. Log & Send
                LogRequest("SUCCESS", user, platform, userProfile, rawInput, detectedInputCode, targetLanguageName, modelUsedForTranslation, promptForLog, finalMessage, daily, minute);
                SendLongMessage(finalMessage, platform);
            }
        }
        catch (Exception ex)
        {
            // Global Error Handler
            CPH.LogError($"[Translation.Bot] TOP LEVEL EXCEPTION: {ex}");
            string errorKey = isAprilFools ? "aprilFoolsApiError" : "apiError";
            LogRequest("EXCEPTION", user, platform, userProfile, rawInput, "N/A", "N/A", modelUsedForTranslation, promptForLog, ex.ToString(), 0, 0);
            SendMessageWithStyle(errorKey, userProfile, platform, user);
        }

        return true;
    }

    // --- HELPER METHODS ---
    // Retrieves a localized message string and replaces placeholders {0}, {1} etc.
    private string GetBotMessage(string baseKey, UserProfile profile, Dictionary<string, object> args)
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

        object[] positionalArgs = new object[maxIndex + 1];
        if (maxIndex > -1)
        {
            for (int i = 0; i <= maxIndex; i++)
            {
                string key = i.ToString();
                positionalArgs[i] = args.ContainsKey(key) ? args[key] : "";
            }
        }

        return BotHelpers.GetBotMessage(profile, baseKey, messageTemplates, positionalArgs);
    }

    // Overload for getting messages without arguments (used for Quotes)
    private string GetBotMessage(string baseKey, UserProfile profile)
    {
        return BotHelpers.GetBotMessage(profile, baseKey, messageTemplates, new object[0]);
    }

    // Wraps CPH methods to send to correct platform
    private void SendMessage(string message, string platform)
    {
        if (platform == "youtube")
            CPH.SendYouTubeMessage(message);
        else
            CPH.SendMessage(message);
    }

    // Syntactic sugar to get a styled message and send it immediately
    private void SendMessageWithStyle(string baseKey, UserProfile profile, string platform, params object[] args)
    {
        var messageArgs = new Dictionary<string, object>();
        for (int i = 0; i < args.Length; i++)
        {
            messageArgs[i.ToString()] = args[i];
        }

        string messageBody = GetBotMessage(baseKey, profile, messageArgs);
        SendMessage(messageBody, platform);
    }

    // --- PROMPT ENGINEERING ---
    // Constructs the specific instructions sent to Gemini API
    private string CreateTranslationPrompt(TextSegment segment, string user, string targetLanguage, string detectedInputCode)
    {
        var mainInstruction = new StringBuilder();
        // Instruction set 1: Detection vs Translation
        if (detectedInputCode == "und")
        {
            mainInstruction.Append("You are a language analysis bot. Your primary task is to distinguish between recognizable human language and random gibberish.");
            mainInstruction.Append(" CRITICAL RULE: If the user's text below is unrecognizable gibberish, you MUST respond with: UNDEF.");
            mainInstruction.AppendFormat(" If it IS recognizable as a real language, translate it into {0}.", targetLanguage);
        }
        else
        {
            mainInstruction.AppendFormat("You are an expert translation bot. Translate the following text into {0}.", targetLanguage);
        }

        mainInstruction.Append(" Ensure the final translation is grammatically complete and proper, starting with a capital letter and ending with appropriate punctuation.");
        // Instruction set 2: Gender/Pronouns
        var genderInstructionBuilder = new StringBuilder();
        if (segment.ExplicitPronouns.Any())
        {
            // Handle placeholders [P1]
            genderInstructionBuilder.Append($" The text contains placeholders like [P1]. Translate the surrounding text to match the gender of the corresponding pronoun: [{string.Join("; ", segment.ExplicitPronouns.Select(kvp => $"{kvp.Key} = '{kvp.Value.Pronoun}'"))}].");
            genderInstructionBuilder.Append(" CRITICAL: You MUST KEEP the placeholders like [P1] in your final translated response.");
        }

        if (segment.SpeakerPronoun != null && !string.IsNullOrEmpty(segment.SpeakerPronoun.Pronoun))
        {
            // Apply speaker's preferred pronouns
            genderInstructionBuilder.Append($" GENDER INSTRUCTIONS: The person speaking is '{user}', and their pronouns are '{segment.SpeakerPronoun.Pronoun}'. If the text contains first-person references (like 'I', 'me', 'my'), apply these pronouns to the speaker.");
            // Add specific language grammar rules for pronouns if defined in config
            if (config.LanguagePronounMap.TryGetValue(targetLanguage, out var targetPronounMap))
            {
                if (targetPronounMap.TryGetValue(segment.SpeakerPronoun.Pronoun, out string instruction))
                    genderInstructionBuilder.Append($" {instruction}");
            }
        }
        else
        {
            // Default to neutral
            genderInstructionBuilder.Append(" GENDER INSTRUCTIONS: The gender of the speaker is unknown. You MUST use gender-neutral phrasing (e.g., singular 'they' in English, or equivalent neutral forms) whenever grammatical gender is ambiguous for the speaker.");
        }

        // Instruction set 3: Proper Nouns (Do not translate)
        string properNounInstruction = segment.ProperNouns.Any() ? $" PROPER NOUNS: The following words are proper nouns and MUST NOT be translated: [{string.Join(", ", segment.ProperNouns)}]." : "";
        // Instruction set 4: Tone/Style
        string toneInstruction = "";
        if (segment.Tone != "neutral")
        {
            string readableTone = segment.Tone;
            if (config.LanguageMap.ContainsKey(segment.Tone))
                readableTone = config.LanguageMap[segment.Tone];
            toneInstruction = $" TONE INSTRUCTION: The final translation MUST be in a '{readableTone}' style.";
        }

        return $"{mainInstruction.ToString()}{genderInstructionBuilder.ToString()}{properNounInstruction}{toneInstruction} Provide only the translation... --- TEXT TO TRANSLATE --- {segment.TextForApi}";
    }

    // Executes the HTTP Post request to Google API
    private string PerformApiCall(HttpClient client, string url, string prompt, string apiKey)
    {
        var payload = new
        {
            contents = new[]
            {
                new
                {
                    parts = new[]
                    {
                        new
                        {
                            text = prompt
                        }
                    }
                }
            }
        };
        string jsonPayload = JsonConvert.SerializeObject(payload);
        var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");
        try
        {
            var response = client.PostAsync(url, content).Result;
            string responseString = response.Content.ReadAsStringAsync().Result;
            if (!response.IsSuccessStatusCode)
                return string.Empty;
            // Parse JSON response to extract text
            JObject jsonResponse = JObject.Parse(responseString);
            var candidates = jsonResponse["candidates"];
            if (candidates != null && candidates.HasValues)
            {
                // Check for content filters
                if (candidates[0]["finishReason"]?.ToString() == "SAFETY")
                    return string.Empty;
                return candidates[0]["content"]["parts"][0]["text"].ToString().Trim();
            }

            return string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    // Writes logs to text file
    private void LogToFile(string status, string message)
    {
        try
        {
            File.AppendAllText(logFilePath, $"{DateTime.Now} [!tr] [{status}] {message}\n");
        }
        catch
        {
        }
    }

    // Checks against the blocklist array from config
    private bool IsInputBlocked(string input, string user, UserProfile profile, string platform, bool isAprilFools)
    {
        if (config.WordBlocklist.Any(word => !string.IsNullOrEmpty(word) && input.ToLower().Contains(word.ToLower())))
        {
            string blockedKey = isAprilFools ? "aprilFoolsBlocked" : "blocked";
            SendMessageWithStyle(blockedKey, profile, platform, user);
            return true;
        }

        return false;
    }

    // Ensures we only use valid 2-3 letter ISO codes, otherwise fallback to "und" (undefined)
    private string SanitizeLanguageCode(string rawCode)
    {
        string cleanedCode = rawCode.Trim().ToLower();
        if (Regex.IsMatch(cleanedCode, @"^[a-z]{2,3}$"))
            return cleanedCode;
        return "und";
    }

    // Splits messages into chunks if they exceed Twitch/YouTube character limits
    private void SendLongMessage(string message, string platform)
    {
        if (string.IsNullOrEmpty(message))
            return;
        int charLimit = (platform == "youtube") ? YOUTUBE_CHAR_LIMIT : TWITCH_CHAR_LIMIT;
        if (message.Length <= charLimit)
        {
            SendMessage(message, platform);
            return;
        }

        var chunks = new List<string>();
        string remainingMessage = message;
        while (remainingMessage.Length > 0)
        {
            int maxChunkLength = charLimit - 7; // Reserve space for "(1/3)"
            if (remainingMessage.Length <= maxChunkLength)
            {
                chunks.Add(remainingMessage);
                break;
            }

            // Attempt to split at space to avoid cutting words
            int splitIndex = remainingMessage.LastIndexOf(' ', maxChunkLength);
            if (splitIndex == -1)
                splitIndex = maxChunkLength;
            chunks.Add(remainingMessage.Substring(0, splitIndex));
            remainingMessage = remainingMessage.Substring(splitIndex).Trim();
        }

        for (int i = 0; i < chunks.Count; i++)
        {
            SendMessage($"({i + 1}/{chunks.Count}) {chunks[i]}", platform);
            CPH.Wait(500); // Small delay to prevent spam flags
        }
    }

    // Looks up the translated name of a language (e.g., getting "Spanish" or "Español")
    private string GetLocalizedLanguageName(string langCode, UserProfile profile)
    {
        string keyToFind = $"{langCode}_normal";
        // Check local language templates first, then fallback to English
        if (messageTemplates.TryGetValue(profile.SpeakingLanguage, out var langTemplates) && langTemplates.TryGetValue(keyToFind, out var localizedName))
            return localizedName;
        if (messageTemplates.TryGetValue("en", out var enTemplates) && enTemplates.TryGetValue(keyToFind, out localizedName))
            return localizedName;
        if (config.LanguageMap.TryGetValue(langCode, out string englishName))
            return englishName;
        return langCode;
    }

    // Converts various input styles (he/him, she, they) into standard forms for the AI
    private string NormalizePronoun(string pronounInput)
    {
        string inputLower = pronounInput.ToLower();
        var neutralKeywords = new List<string>();
        var feminineKeywords = new List<string>();
        var masculineKeywords = new List<string>();
        // Build keyword lists from Config
        foreach (var langEntry in config.PronounNormalizationMap)
        {
            foreach (var keywordEntry in langEntry.Value)
            {
                if (keywordEntry.Value == "they/them")
                    neutralKeywords.Add(keywordEntry.Key);
                else if (keywordEntry.Value == "she/her")
                    feminineKeywords.Add(keywordEntry.Key);
                else if (keywordEntry.Value == "he/him")
                    masculineKeywords.Add(keywordEntry.Key);
            }
        }

        if (neutralKeywords.Any(k => Regex.IsMatch(inputLower, $@"\b{Regex.Escape(k)}\b")))
            return "they/them";
        if (feminineKeywords.Any(k => Regex.IsMatch(inputLower, $@"\b{Regex.Escape(k)}\b")))
            return "she/her";
        if (masculineKeywords.Any(k => Regex.IsMatch(inputLower, $@"\b{Regex.Escape(k)}\b")))
            return "he/him";
        return "they/them"; // Default fallback
    }

    // Validates API usage against Daily/Minute quotas defined in Config
    // Uses CPH.GetGlobalVar/SetGlobalVar to persist counts across execution instances
    private bool IsRequestAllowed(int requestCount, string user, bool isFinalCheck, string modelName, out int dailyCount, out int minuteCount, UserProfile profile, string platform, bool isAprilFools)
    {
        // 1. Determine timezone (Aim for PST/PDT to match API quotas usually, or UTC)
        TimeZoneInfo pstZone;
        try
        {
            pstZone = TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time");
        }
        catch
        {
            try
            {
                pstZone = TimeZoneInfo.FindSystemTimeZoneById("America/Los_Angeles");
            }
            catch
            {
                pstZone = TimeZoneInfo.Utc;
            }
        }

        DateTime pstNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, pstZone);
        bool isPro = modelName == GEMINI_PRO_MODEL;
        int maxPerMinute = isPro ? config.ApiLimits.Pro_RequestsPerMinute : config.ApiLimits.Flash_RequestsPerMinute;
        int maxPerDay = isPro ? config.ApiLimits.Pro_RequestsPerDay : config.ApiLimits.Flash_RequestsPerDay;
        string modelPrefix = isPro ? "pro" : "flash";
        string dailyKey = $"gemini_daily_count_{modelPrefix}_{pstNow:yyyy-MM-dd}";
        string minuteKey = $"gemini_minute_count_{modelPrefix}_{pstNow:yyyy-MM-dd-HH-mm}";
        // Get current counts (persist = true)
        dailyCount = CPH.GetGlobalVar<int>(dailyKey, true) + requestCount;
        minuteCount = CPH.GetGlobalVar<int>(minuteKey, true) + requestCount;
        if (dailyCount > maxPerDay)
        {
            if (isFinalCheck)
            {
                string limitKey = isAprilFools ? "aprilFoolsApiError" : "dailyLimit";
                SendMessageWithStyle(limitKey, profile, platform, user);
            }

            return false;
        }

        if (minuteCount > maxPerMinute)
        {
            if (isFinalCheck)
            {
                string limitKey = isAprilFools ? "aprilFoolsRateLimit" : "rateLimit";
                SendMessageWithStyle(limitKey, profile, platform, user);
            }

            return false;
        }

        // Commit new counts to global variables if this is the final check before execution
        if (isFinalCheck)
        {
            CPH.SetGlobalVar(dailyKey, dailyCount, true);
            CPH.SetGlobalVar(minuteKey, minuteCount, true);
            // Used by a separate CPH action to clean up old keys
            CPH.SetGlobalVar($"gemini_minute_count_reset_{modelPrefix}_{pstNow:yyyy-MM-dd-HH-mm}", minuteCount, false);
            CPH.RunAction("Expire Gemini Minute Counter", false);
        }

        return true;
    }

    // Detailed structured logging for debugging
    private void LogRequest(string status, string user, string platform, UserProfile profile, string originalInput, string detectedLang, string targetLang, string modelUsed, string prompt, string finalOutput, int daily, int minute)
    {
        if (profile == null)
            profile = new UserProfile();
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Timestamp: {DateTime.Now:yyyy-MM-dd HH:mm:ss} [{status}]");
            sb.AppendLine($"User: {user} on {platform.ToUpper()}");
            sb.AppendLine($"User Profile: [Target: {profile.TargetLanguage}, Speaking: {profile.SpeakingLanguage}, Style: {profile.SpeakingStyle}, Pronouns: {profile.Pronouns}]");
            sb.AppendLine($"Original Input: {originalInput}");
            sb.AppendLine($"Detected Input Language: {detectedLang} (using {GEMINI_FLASH_MODEL})");
            sb.AppendLine($"Final Target Language: {targetLang}");
            sb.AppendLine($"Translation Model Used: {modelUsed}");
            sb.AppendLine("--- PROMPT SENT TO AI ---");
            sb.AppendLine(prompt);
            sb.AppendLine("-------------------------");
            sb.AppendLine($"Final Output / Error: {finalOutput}");
            if (status.StartsWith("SUCCESS"))
                sb.AppendLine($"API Usage After Request: Daily={daily}, Per-Minute={minute}");
            sb.AppendLine(new string ('=', 40));
            File.AppendAllText(logFilePath, sb.ToString());
        }
        catch (Exception ex)
        {
            CPH.LogError($"[Translation.Bot] CRITICAL FAILURE TO WRITE TO LOG FILE: {ex.Message}");
        }
    }
}