import json
import os
import copy
from pathlib import Path
from typing import Dict, Any, Optional, Tuple, List

"""
TRANSLATION.BOT LOCALIZATION MANAGER (Open Source Version)
----------------------------------------------------------
This script manages JSON translation files for the TranslationBot.
It compares your local translation file against the Master English Templates.
If keys are missing in a target language, it generates a prompt optimized for 
LLMs (ChatGPT/Claude) to help you translate them, and patches the JSON file.
"""

# --- MASTER CONFIGURATION ---

# 1. FORCED RETRANSLATION LIST
# Any key added to this list will be DELETED from non-English languages in memory
# when the script runs. This forces the script to ask the AI for a fresh translation.
# Keep this empty [] unless you specifically want to regenerate a specific line.
KEYS_TO_FORCE_RETRANSLATION = []

# 2. SUPPORTED LANGUAGES
ALL_SUPPORTED_LANGUAGES = [
    "en", "pt", "ptpt", "es", "fr", "de", "it", "nl", "pl", "sv", "no", "fi", "da", "ja", "ko", "zh",
    "hi", "ar", "vi", "th", "id", "ru", "tr", "he", "el", "ht", "sw", "uk", "tl", "cs", "bg", "hu",
    "is", "ms", "fa", "ro", "bn"
]

# 3. COMMAND ALIASES
# How the !translatehelp command is localized in different regions.
COMMAND_ALIASES = {
    "en": "!translatehelp", "pt": "!ajudatraducao", "ptpt": "!ajudatraducao", "es": "!ayudatraduccion",
    "fr": "!aidetraduction", "de": "!übersetzenhilfe", "it": "!aiutotraduzione", "nl": "!vertaalhulp",
    "pl": "!tłumaczeniepomoc", "sv": "!översätthjälp", "no": "!oversetthjelp", "fi": "!käännösapu",
    "da": "!oversæthjælp", "ja": "!翻訳ヘルプ", "ko": "!번역도움말", "zh": "!翻译帮助",
    "hi": "!अनुवादमदद", "ar": "!مساعدة_الترجمة", "vi": "!trợgiúpdịch", "th": "!ช่วยแปล",
    "id": "!bantuanterjemah", "ru": "!переводпомощь", "tr": "!çeviriyardım", "he": "!תרגוםעזרה",
    "el": "!βοήθειαμετάφρασης", "ht": "!edtradiksyon", "sw": "!msaadatafsiri", "uk": "!допомогапереклад",
    "tl": "!tulongsaling", "cs": "!prekladpomoc", "bg": "!преводпомощ", "hu": "!forditassegitseg",
    "is": "!thyðingahjalp", "ms": "!bantuanterjemah", "fa": "!راهنمای_ترجمه", "ro": "!ajutortraducere",
    "bn": "!অনুবাদসাহায্য"
}

# 4. STYLE MAPPING
# Maps localized/native words (keys) to internal style IDs (values).
STYLE_MAP = {
    "en": {"normal": "normal", "pirate": "pirate", "yoda": "yoda", "shakes": "shakes", "archaic": "shakes", "old": "shakes", "dk": "dk", "donkeykong": "dk", "baby": "baby"},
    "pt": {"normal": "normal", "pirata": "pirate", "yoda": "yoda", "arcaico": "shakes", "antigo": "shakes", "dk": "dk", "donkeykong": "dk", "bebê": "baby", "bebe": "baby"},
    "ptpt": {"normal": "normal", "pirata": "pirate", "yoda": "yoda", "arcaico": "shakes", "antigo": "shakes", "dk": "dk", "donkeykong": "dk", "bebé": "baby", "bebe": "baby"},
    "es": {"normal": "normal", "pirata": "pirate", "yoda": "yoda", "antiguo": "shakes", "arcaico": "shakes", "dk": "dk", "donkeykong": "dk", "bebé": "baby", "bebe": "baby"},
    "fr": {"normal": "normal", "pirate": "pirate", "yoda": "yoda", "ancien": "shakes", "classique": "shakes", "dk": "dk", "donkeykong": "dk", "bébé": "baby", "bebe": "baby"},
    "de": {"normal": "normal", "pirat": "pirate", "yoda": "yoda", "altdeutsch": "shakes", "archaisch": "shakes", "dk": "dk", "donkeykong": "dk", "baby": "baby"},
    "it": {"normale": "normal", "pirata": "pirate", "yoda": "yoda", "antico": "shakes", "arcaico": "shakes", "dk": "dk", "donkeykong": "dk", "bambino": "baby"},
    "nl": {"normaal": "normal", "piraat": "pirate", "yoda": "yoda", "ouderwets": "shakes", "archaïsch": "shakes", "archaisch": "shakes", "dk": "dk", "donkeykong": "dk", "baby": "baby"},
    "pl": {"normalny": "normal", "pirat": "pirate", "yoda": "yoda", "archaiczny": "shakes", "staropolski": "shakes", "dk": "dk", "donkeykong": "dk", "dziecko": "baby"},
    "sv": {"normal": "normal", "pirat": "pirate", "yoda": "yoda", "ålderdomlig": "shakes", "alderdomlig": "shakes", "gammaldags": "shakes", "dk": "dk", "donkeykong": "dk", "bebis": "baby"},
    "no": {"normal": "normal", "pirat": "pirate", "yoda": "yoda", "gammeldags": "shakes", "arkaisk": "shakes", "dk": "dk", "donkeykong": "dk", "baby": "baby"},
    "fi": {"normaali": "normal", "merirosvo": "pirate", "yoda": "yoda", "vanhaikainen": "shakes", "arkaainen": "shakes", "dk": "dk", "donkeykong": "dk", "vauva": "baby"},
    "da": {"normal": "normal", "pirat": "pirate", "yoda": "yoda", "gammeldags": "shakes", "arkaisk": "shakes", "dk": "dk", "donkeykong": "dk", "baby": "baby"},
    "ja": {"通常": "normal", "海賊": "pirate", "ヨーダ": "yoda", "古風": "shakes", "古語": "shakes", "時代劇": "shakes", "ドンキーコング": "dk", "赤ちゃん": "baby", "tsuujou": "normal", "kaizoku": "pirate", "yooda": "yoda", "kofuu": "shakes", "kogo": "shakes", "jidaigeki": "shakes", "donkiikongu": "dk", "akachan": "baby"},
    "ko": {"일반": "normal", "해적": "pirate", "요다": "yoda", "사극": "shakes", "옛날말투": "shakes", "동키콩": "dk", "아기": "baby", "ilban": "normal", "haejeok": "pirate", "yennalmaltoo": "shakes", "sageuk": "shakes", "dongkikong": "dk", "agi": "baby"},
    "zh": {"普通": "normal", "海盗": "pirate", "尤达": "yoda", "古文": "shakes", "古风": "shakes", "大金刚": "dk", "婴儿": "baby", "putong": "normal", "haidao": "pirate", "youda": "yoda", "guwen": "shakes", "gufeng": "shakes", "dajingang": "dk", "yinger": "baby"},
    "hi": {"सामान्य": "normal", "समुद्रीडाकू": "pirate", "योडा": "yoda", "प्राचीन": "shakes", "पुराना": "shakes", "डीके": "dk", "बच्चा": "baby", "samanya": "normal", "samudridaku": "pirate", "prachin": "shakes", "purana": "shakes", "dike": "dk", "bachcha": "baby"},
    "ar": {"عادي": "normal", "قرصان": "pirate", "يودا": "yoda", "فصيح": "shakes", "قديم": "shakes", "دونكي كونج": "dk", "رضيع": "baby", "aadi": "normal", "qursan": "pirate", "fasih": "shakes", "qadim": "shakes", "dunkikung": "dk", "radi": "baby"},
    "vi": {"thường": "normal", "thuong": "normal", "cướpbiển": "pirate", "cuopbien": "pirate", "yoda": "yoda", "cổ": "shakes", "co": "shakes", "cổxưa": "shakes", "coxua": "shakes", "dk": "dk", "embé": "baby", "embe": "baby"},
    "th": {"ปกติ": "normal", "โจรสลัด": "pirate", "โยดา": "yoda", "โบราณ": "shakes", "ดองกีคอง": "dk", "ทารก": "baby", "pakati": "normal", "chonsalat": "pirate", "boran": "shakes", "dongkikhong": "dk", "tharok": "baby"},
    "id": {"normal": "normal", "bajaklaut": "pirate", "yoda": "yoda", "kuno": "shakes", "arkais": "shakes", "dk": "dk", "bayi": "baby"},
    "ru": {"обычный": "normal", "пират": "pirate", "йода": "yoda", "старинный": "shakes", "архаичный": "shakes", "дк": "dk", "младенец": "baby", "obychnyy": "normal", "pirat": "pirate", "starinnyy": "shakes", "arkhaichnyy": "shakes", "mladenets": "baby"},
    "tr": {"normal": "normal", "korsan": "pirate", "yoda": "yoda", "arkaik": "shakes", "eski": "shakes", "dk": "dk", "bebek": "baby"},
    "he": {"רגיל": "normal", "פיראט": "pirate", "יודה": "yoda", "עתיק": "shakes", "ארכאי": "shakes", "דונקי קונג": "dk", "תינוק": "baby", "ragil": "normal", "pirat": "pirate", "atik": "shakes", "arkhai": "shakes", "donkikong": "dk", "tinok": "baby"},
    "el": {"κανονικό": "normal", "πειρατής": "pirate", "γιόντα": "yoda", "αρχαϊκό": "shakes", "παλιό": "shakes", "ντόνκι κονγκ": "dk", "μωρό": "baby", "kanoniko": "normal", "peiratis": "pirate", "gionta": "yoda", "archaiko": "shakes", "palio": "shakes", "ntonkikonk": "dk", "moro": "baby"},
    "ht": {"nòmal": "normal", "nomal": "normal", "pirat": "pirate", "yoda": "yoda", "ansyen": "shakes", "vye": "shakes", "dk": "dk", "tibebe": "baby"},
    "sw": {"kawaida": "normal", "mharamia": "pirate", "yoda": "yoda", "kikale": "shakes", "kizamani": "shakes", "dk": "dk", "mtoto": "baby"},
    "uk": {"звичайний": "normal", "пірат": "pirate", "йода": "yoda", "старовинний": "shakes", "архаїчний": "shakes", "дк": "dk", "немовля": "baby", "zvychaynyy": "normal", "pirat": "pirate", "starovynnyy": "shakes", "arkhayichnyy": "shakes", "nemovlya": "baby"},
    "tl": {"normal": "normal", "pirata": "pirate", "yoda": "yoda", "makaluma": "shakes", "sinauna": "shakes", "dk": "dk", "sanggol": "baby"},
    "cs": {"normální": "normal", "normalni": "normal", "pirát": "pirate", "pirat": "pirate", "yoda": "yoda", "archaický": "shakes", "archaicky": "shakes", "starobylý": "shakes", "starobyly": "shakes", "dk": "dk", "dítě": "baby", "dite": "baby"},
    "bg": {"нормален": "normal", "пират": "pirate", "йода": "yoda", "архаичен": "shakes", "старинен": "shakes", "дк": "dk", "бебе": "baby", "normalen": "normal", "pirat": "pirate", "arhaichen": "shakes", "starinen": "shakes", "bebe": "baby"},
    "hu": {"normál": "normal", "normal": "normal", "kalóz": "pirate", "kaloz": "pirate", "yoda": "yoda", "régies": "shakes", "regies": "shakes", "archaizáló": "shakes", "archaizalo": "shakes", "dk": "dk", "baba": "baby"},
    "is": {"venjulegur": "normal", "sjóræningi": "pirate", "sjoraeningi": "pirate", "yoda": "yoda", "forn": "shakes", "gamaldags": "shakes", "dk": "dk", "barn": "baby"},
    "ms": {"biasa": "normal", "lanun": "pirate", "yoda": "yoda", "kuno": "shakes", "lama": "shakes", "dk": "dk", "bayi": "baby"},
    "fa": {"عادی": "normal", "دزددریایی": "pirate", "یودا": "yoda", "کهن": "shakes", "باستانی": "shakes", "دی‌کی": "dk", "بچه": "baby", "aadi": "normal", "dozdedaryayi": "pirate", "kohan": "shakes", "bastani": "shakes", "dikey": "dk", "bachche": "baby"},
    "ro": {"normal": "normal", "pirat": "pirate", "yoda": "yoda", "arhaic": "shakes", "vechi": "shakes", "dk": "dk", "bebeluș": "baby", "bebelus": "baby"},
    "bn": {"সাধারণ": "normal", "জলদস্যু": "pirate", "যোডা": "yoda", "প্রাচীন": "shakes", "পুরানো": "shakes", "ডিকে": "dk", "শিশু": "baby", "shadharon": "normal", "jolodossu": "pirate", "joda": "yoda", "prachin": "shakes", "purano": "shakes", "dike": "dk", "shishu": "baby"}
}

# --- TEMPLATES ---
# These dictionaries define the "Source of Truth". 
# If a key exists here, it must exist in all other languages.

ADMIN_STRINGS_EN = {
    "adminBlockAlreadyExists_normal": "@{0}, the user {1} is already on the blocklist.", "adminBlockAlreadyExists_pirate": "Belay that, @{0}! {1} is already in the brig!", "adminBlockAlreadyExists_yoda": "Already on the blocklist, {1} is, @{0}.", "adminBlockAlreadyExists_shakes": "Hold, @{0}! The user {1} is already barred.", "adminBlockAlreadyExists_dk": "OOK? @{0}? (Points at {1}, confused) OOK.", "adminBlockAlreadyExists_baby": "{1} already in timeout, @{0}!",
    "adminBlockConfirm_normal": "@{0}, the user {1} has been blocked from using translation commands.", "adminBlockConfirm_pirate": "Aye, @{0}! The scallywag {1} has been sent to the brig!", "adminBlockConfirm_yoda": "Blocked from commands, the user {1} is, @{0}.", "adminBlockConfirm_shakes": "Hark, @{0}! The user {1} is henceforth barred from these commands.", "adminBlockConfirm_dk": "OOK! @{0}! (Thumbs down for {1})", "adminBlockConfirm_baby": "No more talky for {1}, @{0}!",
    "adminBlockNoUser_normal": "@{0}, you must specify a username to block.", "adminBlockNoUser_pirate": "Arrr, @{0}! Ye must name the scallywag ye wish to block!", "adminBlockNoUser_yoda": "A username, specify you must, @{0}.", "adminBlockNoUser_shakes": "Pray, @{0}, name the soul thou wouldst block!", "adminBlockNoUser_dk": "OOK! @{0}! (Who block?) OOK!", "adminBlockNoUser_baby": "@{0}, who block? Need name!",
    "adminClearBlocklistConfirm_normal": "@{0}, the blocklist has been cleared.", "adminClearBlocklistConfirm_pirate": "Aye, @{0}! The blacklist be scrubbed clean as the deck!", "adminClearBlocklistConfirm_yoda": "Cleared, the blocklist is, @{0}.", "adminClearBlocklistConfirm_shakes": "It is done, @{0}! The scroll of the banned hath been purged.", "adminClearBlocklistConfirm_dk": "OOK! @{0}! (Wipes the slate clean) OOK OOK!", "adminClearBlocklistConfirm_baby": "All gone, @{0}! Bad list is all clean now!",
    "adminClearBlocklistEmpty_normal": "@{0}, the blocklist is currently empty.", "adminClearBlocklistEmpty_pirate": "Avast, @{0}! The blacklist be already empty!", "adminClearBlocklistEmpty_yoda": "Empty, the blocklist already is, @{0}.", "adminClearBlocklistEmpty_shakes": "Forsooth, @{0}, the ledger of the barred is already barren.", "adminClearBlocklistEmpty_dk": "OOK? @{0}? (Scratches head, holds up empty banana peel)", "adminClearBlocklistEmpty_baby": "Nothing there, @{0}! List is already empty!",
    
    "adminUnblockConfirm_normal": "{gender, select, male {@{0}, the user {1} has been unblocked.} female {@{0}, the user {1} has been unblocked.} other {@{0}, the user {1} has been unblocked.}}",
    "adminUnblockConfirm_pirate": "{gender, select, male {Aye, @{0}! The scallywag {1} has been freed from the brig!} female {Aye, @{0}! The scallywag {1} has been freed from the brig!} other {Aye, @{0}! The scallywag {1} has been freed from the brig!}}",
    "adminUnblockConfirm_yoda": "{gender, select, male {Unblocked, the user {1} is, @{0}.} female {Unblocked, the user {1} is, @{0}.} other {Unblocked, the user {1} is, @{0}.}}",
    "adminUnblockConfirm_shakes": "{gender, select, male {Hark, @{0}! The user {1} is once more free to command.} female {Hark, @{0}! The user {1} is once more free to command.} other {Hark, @{0}! The user {1} is once more free to command.}}",
    
    "adminUnblockConfirm_dk": "OOK! @{0}! (Thumbs up for {1})", 
    "adminUnblockConfirm_baby": "Okay now, @{0}! {1} can talky again!",

    "adminUnblockNotFound_normal": "@{0}, the user {1} was not found on the blocklist.", "adminUnblockNotFound_pirate": "Arrr, @{0}! I can't find {1} in the brig!", "adminUnblockNotFound_yoda": "On the blocklist, {1} was not found, @{0}.", "adminUnblockNotFound_shakes": "Forsooth, @{0}, the user {1} was not among the barred.", "adminUnblockNotFound_dk": "OOK? @{0}? (Looks for {1}, shrugs)", "adminUnblockNotFound_baby": "No find {1}, @{0}! Not in timeout!",
    "adminUnblockNoUser_normal": "@{0}, you must specify a username to unblock.", "adminUnblockNoUser_pirate": "Arrr, @{0}! Ye must name the soul ye wish to set free!", "adminUnblockNoUser_yoda": "A username, specify you must, @{0}, to unblock.", "adminUnblockNoUser_shakes": "Pray, @{0}, name the soul thou wouldst release!", "adminUnblockNoUser_dk": "OOK! @{0}! (Who free?) OOK!", "adminUnblockNoUser_baby": "@{0}, who free? Need name!",
}

GENERAL_UI_STRINGS_EN = {
    "alreadyTranslated_normal": "That message is already in the target language!", 
    "alreadyTranslated_pirate": "Shiver me timbers! That be the tongue we're sailin' to already!", 
    "alreadyTranslated_yoda": "In the target language, that message already is. Hmmm.", 
    "alreadyTranslated_shakes": "Hold, for thy message is already writ in the desired parlance!", 
    "alreadyTranslated_dk": "OOK! (Points, nods) OOK OOK!", 
    "alreadyTranslated_baby": "Already that talky! No need change!",
    
    "apiError_normal": "Sorry, a Translation.Bot error occurred.", 
    "apiError_pirate": "Shiver me timbers! The cursed machine has sprung a leak!", 
    "apiError_yoda": "A disturbance in the Force, there is. An error, I sense.", 
    "apiError_shakes": "Alas, a foul error doth plague the machine's very soul!", 
    "apiError_dk": "OOK! OOOOK! (beats chest in frustration)", 
    "apiError_baby": "Uh oh! Translation.Bot go boom-boom!", 

    "blocked_normal": "Sorry, that message cannot be translated.", 
    "blocked_pirate": "Belay that! Those words be forbidden on this ship!", 
    "blocked_yoda": "Translate this, I cannot. A dark path, those words are.", 
    "blocked_shakes": "Hold! Such vulgar parlance shall not pass from my lips!", 
    "blocked_dk": "GRRRR! OOK! (shakes head no)", 
    "blocked_baby": "No-no talky! Bad words!",
    
    "blocklistAddConfirm_normal": "The word {1} has been added to the translation blocklist.", 
    "blocklistAddConfirm_pirate": "Aye, the word {1} has been blacklisted from our parley!", 
    "blocklistAddConfirm_yoda": "To the blocklist, the word {1} added has been.", 
    "blocklistAddConfirm_shakes": "Hark! The word {1} is henceforth forbidden from translation.", 
    "blocklistAddConfirm_dk": "OOK! (Thumbs down for {1}) OOK OOK!", 
    "blocklistAddConfirm_baby": "No more {1}! Bad word!",
    
    "blocklistAlreadyExists_normal": "The word {1} is already in the translation blocklist.", 
    "blocklistAlreadyExists_pirate": "Belay that! The word {1} is already on the blacklist!", 
    "blocklistAlreadyExists_yoda": "Already on the blocklist, the word {1} is.", 
    "blocklistAlreadyExists_shakes": "Hold! The word {1} is already proscribed!", 
    "blocklistAlreadyExists_dk": "Ook? {1}? Ook.", 
    "blocklistAlreadyExists_baby": "{1} already no-no word!",
    
    "blockListUsers_normal": "Blocked users: {1}", 
    "blockListUsers_pirate": "Here be the scallywags in the brig: {1}", 
    "blockListUsers_yoda": "In the blocklist, these users are: {1}", 
    "blockListUsers_shakes": "Behold, the list of the barred: {1}", 
    "blockListUsers_dk": "OOK! (Points at list of blocked monkeys: {1})", 
    "blockListUsers_baby": "These are the no-no people: {1}",
    
    "blockListUsersEmpty_normal": "The user blocklist is currently empty.", 
    "blockListUsersEmpty_pirate": "The brig be empty! Not a single soul is locked away.", 
    "blockListUsersEmpty_yoda": "Empty, the blocklist is.", 
    "blockListUsersEmpty_shakes": "Forsooth, the list of the barred is barren.", 
    "blockListUsersEmpty_dk": "OOK! (Shows empty banana peel)", 
    "blockListUsersEmpty_baby": "No no-no people! All friends!",
    
    "blockListWords_normal": "Blocked words: {1}", 
    "blockListWords_pirate": "Here be the forbidden parley: {1}", 
    "blockListWords_yoda": "Forbidden, these words are: {1}", 
    "blockListWords_shakes": "Behold, the list of proscribed words: {1}", 
    "blockListWords_dk": "OOK! (Points at list of bad bananas: {1})", 
    "blockListWords_baby": "These are the no-no words: {1}",
    
    "blockListWordsEmpty_normal": "The word blocklist is currently empty.", 
    "blockListWordsEmpty_pirate": "There be no forbidden words on this ship!", 
    "blockListWordsEmpty_yoda": "Empty, the word blocklist is.", 
    "blockListWordsEmpty_shakes": "Forsooth, no words have been proscribed.", 
    "blockListWordsEmpty_dk": "OOK! (Shows clean slate)", 
    "blockListWordsEmpty_baby": "No no-no words! All talkies okay!",
    
    "blocklistNoWord_normal": "You need to specify a word to block or unblock!", 
    "blocklistNoWord_pirate": "Avast! Ye must name the word ye wish to cast into the sea!", 
    "blocklistNoWord_yoda": "A word, specify you must.", 
    "blocklistNoWord_shakes": "Pray, specify the word thou wishest to affect!", 
    "blocklistNoWord_dk": "OOK? (What word?)", 
    "blocklistNoWord_baby": "What word? Need word!",
    
    "blocklistNotFound_normal": "The word {1} was not found in the translation blocklist.", 
    "blocklistNotFound_pirate": "Arrr, the word {1} was not on the blacklist to begin with!", 
    "blocklistNotFound_yoda": "On the blocklist, the word {1} was not found.", 
    "blocklistNotFound_shakes": "Forsooth, the word {1} was not found among the proscribed terms!", 
    "blocklistNotFound_dk": "Ook? {1}? (shrugs)", 
    "blocklistNotFound_baby": "No find {1}! Not a no-no word!",
    
    "blocklistRemoveConfirm_normal": "The word {1} has been removed from the translation blocklist.", 
    "blocklistRemoveConfirm_pirate": "Heave ho! The word {1} has been struck from the blacklist!", 
    "blocklistRemoveConfirm_yoda": "From the blocklist, the word {1} removed has been.", 
    "blocklistRemoveConfirm_shakes": "So be it! The proscription against the word {1} is lifted.", 
    "blocklistRemoveConfirm_dk": "OOK! (Thumbs up for {1})", 
    "blocklistRemoveConfirm_baby": "Okay now! Can say {1}!",
    
    "clearConfirm_normal": "Your language preferences have been cleared.", 
    "clearConfirm_pirate": "Heave ho! Yer custom chart has been sent to Davy Jones' Locker.", 
    "clearConfirm_yoda": "Cleared, your preferences are. Forget them, I will.", 
    "clearConfirm_shakes": "Thus, thy linguistic decree is rendered null and void.", 
    "clearConfirm_dk": "OOK! (stomp stomp) OOK OOK!", 
    "clearConfirm_baby": "All gone! Preferences all gone!",
    
    "clearNone_normal": "You did not have a language preference to clear.", 
    "clearNone_pirate": "Avast ye! There be no chart in yer hold to cast overboard!", 
    "clearNone_yoda": "A preference to clear, you have not. Hmmm.", 
    "clearNone_shakes": "Forsooth, thou hadst no established preference to annul.", 
    "clearNone_dk": "Ook? (scratches head)", 
    "clearNone_baby": "No have! No have thing to throw away!",
    
    "confirmPartPronouns_normal": "pronouns to '{0}'", "confirmPartPronouns_pirate": "pronouns to '{0}'", "confirmPartPronouns_yoda": "pronouns to '{0}'", "confirmPartPronouns_shakes": "pronouns to '{0}'", "confirmPartPronouns_dk": "(sets name-sounds to '{0}')", "confirmPartPronouns_baby": "you-words to '{0}'",
    "confirmPartSpeaking_normal": "speaking language to {0}", "confirmPartSpeaking_pirate": "my tongue to {0}", "confirmPartSpeaking_yoda": "my voice to {0}", "confirmPartSpeaking_shakes": "my parlance to {0}", "confirmPartSpeaking_dk": "(changes OOKS to {0})", "confirmPartSpeaking_baby": "my babble-talk to {0}",
    "confirmPartStyle_normal": "style to {0}", "confirmPartStyle_pirate": "swagger to {0}", "confirmPartStyle_yoda": "style to {0}", "confirmPartStyle_shakes": "manner to {0}", "confirmPartStyle_dk": "(sets OOK-style to {0})", "confirmPartStyle_baby": "play-style to {0}",
    "confirmPartTarget_normal": "target language to {0}", "confirmPartTarget_pirate": "new heading to the {0} seas", "confirmPartTarget_yoda": "translation path to {0}", "confirmPartTarget_shakes": "thy course to {0}", "confirmPartTarget_dk": "(sets banana-path to {0})", "confirmPartTarget_baby": "new talky-place to {0}",

    "dailyLimit_normal": "That command is too complex for the remaining daily API limit.", 
    "dailyLimit_pirate": "The rum barrel is empty for today, matey! No more magic 'til sunrise.", 
    "dailyLimit_yoda": "Tired, the Force is. For today, no more power remains.", 
    "dailyLimit_shakes": "Alas, the well of knowledge runs dry for this day! The command is too great a burden.", 
    "dailyLimit_dk": "Oook... (yawns, lies down)", 
    "dailyLimit_baby": "Translation.Bot sleepy... all sleepy... no more thinky-think.", 

    "helpLinkNotFound_normal": "Sorry, I couldn't find a help guide link.", 
    "helpLinkNotFound_pirate": "Shiver me timbers! I can't seem to find the treasure map ye be lookin' for!", 
    "helpLinkNotFound_yoda": "A link to the guide, find it I cannot. Lost, it seems to be.", 
    "helpLinkNotFound_shakes": "Alas, the charter for which thou seekest cannot be found!", 
    "helpLinkNotFound_dk": "Ook? (scratches head, shrugs) Ook ook!", 
    "helpLinkNotFound_baby": "Uh oh! Linky all gone!",
    
    "helpTranslate_normal": "{gender, select, male {@{0}, you need to provide text to translate! For a full guide, type !translatehelp} female {@{0}, you need to provide text to translate! For a full guide, type !translatehelp} other {@{0}, you need to provide text to translate! For a full guide, type !translatehelp}}",
    
    "helpTranslate_pirate": "{gender, select, male {Arrr, @{0}! Ye must give me some words to parley! Try !translatehelp for the full map.} female {Arrr, @{0}! Ye must give me some words to parley! Try !translatehelp for the full map.} other {Arrr, @{0}! Ye must give me some words to parley! Try !translatehelp for the full map.}}",
    
    "helpTranslate_yoda": "{gender, select, male {Provide text, you must, @{0}. Guidance you seek? !translatehelp, you will type.} female {Provide text, you must, @{0}. Guidance you seek? !translatehelp, you will type.} other {Provide text, you must, @{0}. Guidance you seek? !translatehelp, you will type.}}",
    
    "helpTranslate_shakes": "{gender, select, male {Pray, @{0}, bestow upon me some text for which to render! For the full charter, !translatehelp.} female {Pray, @{0}, bestow upon me some text for which to render! For the full charter, !translatehelp.} other {Pray, @{0}, bestow upon me some text for which to render! For the full charter, !translatehelp.}}",
    
    "helpTranslate_dk": "OOK! @{0}! (Need words!) OOK! (Try !translatehelp!)", 
    "helpTranslate_baby": "@{0}, need talkies! Gib talkies! Need help? Try !translatehelp.", 

    "invalidCode_normal": "{0} is not a valid language code.", "invalidCode_pirate": "Arrr, that be no proper heading! {0} is not a known tongue.", "invalidCode_yoda": "A valid code, {0} is not. Choose again, you must.", "invalidCode_shakes": "Fie! The code {0} is but a phantom, unknown to this realm.", "invalidCode_dk": "OOK?! {0}?! (tilts head, confused)", "invalidCode_baby": "No like {0}! Bad talky-word!",
    "