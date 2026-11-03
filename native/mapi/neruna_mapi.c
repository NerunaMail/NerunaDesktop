/*
 * Neruna's Simple MAPI provider (MPL 2.0): programs that "send by e-mail" through the default mail program (accounting,
 * PDF tools, "Senden an → E-Mail-Empfänger") call MAPISendMail in mapi32.dll; Windows passes the call on to the DLL
 * registered for the default mail program – this one.
 *
 * It never sends anything itself: recipients, subject, text and attachments go to Neruna, which opens a new mail.
 * The attachments are copied first (callers often delete their temporary files as soon as the call returns), then
 * neruna.exe is started with a ".nerunamapi" file describing the mail (JSON, UTF-8) – a running Neruna takes it over
 * like a .eml file. The DLL itself sits in a folder of the machine (all users of a terminal server); which neruna.exe
 * belongs to the calling user is read from that user's registration (HKCU\Software\Clients\Mail\Neruna).
 *
 * Built with mingw-w64 for 64 and 32 bit (build.sh); Simple MAPI functions are stdcall ("FAR PASCAL").
 */
#define WIN32_LEAN_AND_MEAN
#define UNICODE
#define _UNICODE
#include <windows.h>
#include <stdarg.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <wchar.h>

#define EXPORT __declspec(dllexport)

#define SUCCESS_SUCCESS 0
#define MAPI_E_FAILURE 2
#define MAPI_E_INSUFFICIENT_MEMORY 5
#define MAPI_E_ATTACHMENT_NOT_FOUND 11
#define MAPI_E_ATTACHMENT_OPEN_FAILURE 12
#define MAPI_E_UNKNOWN_RECIPIENT 14
#define MAPI_E_NOT_SUPPORTED 26

#define MAPI_DIALOG 0x00000008
#define MAPI_TO 1
#define MAPI_CC 2
#define MAPI_BCC 3

typedef ULONG_PTR LHANDLE;

typedef struct {
    ULONG ulReserved;
    ULONG ulRecipClass;
    LPSTR lpszName;
    LPSTR lpszAddress;
    ULONG ulEIDSize;
    LPVOID lpEntryID;
} MapiRecipDescA;

typedef struct {
    ULONG ulReserved;
    ULONG ulRecipClass;
    PWSTR lpszName;
    PWSTR lpszAddress;
    ULONG ulEIDSize;
    LPVOID lpEntryID;
} MapiRecipDescW;

typedef struct {
    ULONG ulReserved;
    ULONG flFlags;
    ULONG nPosition;
    LPSTR lpszPathName;
    LPSTR lpszFileName;
    LPVOID lpFileType;
} MapiFileDescA;

typedef struct {
    ULONG ulReserved;
    ULONG flFlags;
    ULONG nPosition;
    PWSTR lpszPathName;
    PWSTR lpszFileName;
    LPVOID lpFileType;
} MapiFileDescW;

typedef struct {
    ULONG ulReserved;
    LPSTR lpszSubject;
    LPSTR lpszNoteText;
    LPSTR lpszMessageType;
    LPSTR lpszDateReceived;
    LPSTR lpszConversationID;
    ULONG flFlags;
    MapiRecipDescA *lpOriginator;
    ULONG nRecipCount;
    MapiRecipDescA *lpRecips;
    ULONG nFileCount;
    MapiFileDescA *lpFiles;
} MapiMessageA;

typedef struct {
    ULONG ulReserved;
    PWSTR lpszSubject;
    PWSTR lpszNoteText;
    PWSTR lpszMessageType;
    PWSTR lpszDateReceived;
    PWSTR lpszConversationID;
    ULONG flFlags;
    MapiRecipDescW *lpOriginator;
    ULONG nRecipCount;
    MapiRecipDescW *lpRecips;
    ULONG nFileCount;
    MapiFileDescW *lpFiles;
} MapiMessageW;

/* printf into a buffer of `size` characters; false when it did not fit. */
static int format(wchar_t *buffer, size_t size, const wchar_t *pattern, ...)
{
    va_list args;
    va_start(args, pattern);
    int n = _vsnwprintf(buffer, size, pattern, args);
    va_end(args);
    if (n < 0 || (size_t)n >= size) {
        buffer[size - 1] = 0;
        return 0;
    }
    return 1;
}

/* ── A line per call in %TEMP%\\Neruna-MAPI\\mapi.log: which program called what and how it ended (no addresses or
   contents – only names of functions, flags and results). ── */

static void trace(const char *what, ULONG value)
{
    wchar_t temp[MAX_PATH];
    DWORD n = GetTempPathW(MAX_PATH, temp);
    if (n == 0 || n >= MAX_PATH - 32) {
        return;
    }
    wchar_t folder[MAX_PATH];
    wchar_t file[MAX_PATH];
    if (!format(folder, MAX_PATH, L"%lsNeruna-MAPI", temp) || !format(file, MAX_PATH, L"%ls\\mapi.log", folder)) {
        return;
    }
    CreateDirectoryW(folder, NULL);

    char program[MAX_PATH] = "";
    GetModuleFileNameA(NULL, program, MAX_PATH);
    SYSTEMTIME now;
    GetLocalTime(&now);
    char line[MAX_PATH + 160];
    int length = _snprintf(line, sizeof(line) - 1, "%04u-%02u-%02u %02u:%02u:%02u %u-bit pid %lu %s: %s %lu\r\n",
                           now.wYear, now.wMonth, now.wDay, now.wHour, now.wMinute, now.wSecond,
                           (unsigned)(sizeof(void *) * 8), (unsigned long)GetCurrentProcessId(), program, what, (unsigned long)value);
    if (length <= 0) {
        return;
    }
    HANDLE handle = CreateFileW(file, FILE_APPEND_DATA, FILE_SHARE_READ | FILE_SHARE_WRITE, NULL, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
    if (handle != INVALID_HANDLE_VALUE) {
        DWORD written;
        WriteFile(handle, line, (DWORD)length, &written, NULL);
        CloseHandle(handle);
    }
}

BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, LPVOID reserved)
{
    (void)instance; (void)reserved;
    if (reason == DLL_PROCESS_ATTACH) {
        trace("loaded", 0);
    }
    return TRUE;
}

/* ── A growing UTF-16 text (the JSON is built in it, then written as UTF-8) ── */

typedef struct {
    wchar_t *text;
    size_t length;
    size_t capacity;
    int failed;
} Text;

static void append_n(Text *t, const wchar_t *s, size_t n)
{
    if (t->failed || n == 0) {
        return;
    }
    if (t->length + n + 1 > t->capacity) {
        size_t capacity = t->capacity ? t->capacity : 1024;
        while (t->length + n + 1 > capacity) {
            capacity *= 2;
        }
        wchar_t *grown = (wchar_t *)realloc(t->text, capacity * sizeof(wchar_t));
        if (!grown) {
            t->failed = 1;
            return;
        }
        t->text = grown;
        t->capacity = capacity;
    }
    memcpy(t->text + t->length, s, n * sizeof(wchar_t));
    t->length += n;
    t->text[t->length] = 0;
}

static void append(Text *t, const wchar_t *s)
{
    if (s) {
        append_n(t, s, wcslen(s));
    }
}

/* A JSON string: quotes, backslashes and control characters escaped. */
static void append_json(Text *t, const wchar_t *s)
{
    append(t, L"\"");
    for (; s && *s; s++) {
        wchar_t c = *s;
        if (c == L'"') {
            append(t, L"\\\"");
        } else if (c == L'\\') {
            append(t, L"\\\\");
        } else if (c == L'\n') {
            append(t, L"\\n");
        } else if (c == L'\r') {
            append(t, L"\\r");
        } else if (c == L'\t') {
            append(t, L"\\t");
        } else if (c < 0x20) {
            wchar_t escaped[8];
            format(escaped, 8, L"\\u%04x", (unsigned)c);
            append(t, escaped);
        } else {
            append_n(t, &c, 1);
        }
    }
    append(t, L"\"");
}

/* ── One mail, the same for the ANSI and the Unicode calls ── */

typedef struct {
    ULONG kind; /* MAPI_TO, MAPI_CC, MAPI_BCC */
    wchar_t *name;
    wchar_t *address;
} Recipient;

typedef struct {
    wchar_t *path;
    wchar_t *name;
} Attachment;

typedef struct {
    wchar_t *subject;
    wchar_t *body;
    ULONG recipient_count;
    Recipient *recipients;
    ULONG attachment_count;
    Attachment *attachments;
    int dialog;
} Mail;

static wchar_t *copy_w(const wchar_t *s)
{
    if (!s) {
        return NULL;
    }
    size_t n = wcslen(s) + 1;
    wchar_t *copy = (wchar_t *)malloc(n * sizeof(wchar_t));
    if (copy) {
        memcpy(copy, s, n * sizeof(wchar_t));
    }
    return copy;
}

/* ANSI text of the caller (its code page) as UTF-16. */
static wchar_t *from_ansi(const char *s)
{
    if (!s) {
        return NULL;
    }
    int n = MultiByteToWideChar(CP_ACP, 0, s, -1, NULL, 0);
    if (n <= 0) {
        return NULL;
    }
    wchar_t *w = (wchar_t *)malloc((size_t)n * sizeof(wchar_t));
    if (w) {
        MultiByteToWideChar(CP_ACP, 0, s, -1, w, n);
    }
    return w;
}

static void free_mail(Mail *m)
{
    free(m->subject);
    free(m->body);
    for (ULONG i = 0; i < m->recipient_count; i++) {
        free(m->recipients[i].name);
        free(m->recipients[i].address);
    }
    free(m->recipients);
    for (ULONG i = 0; i < m->attachment_count; i++) {
        free(m->attachments[i].path);
        free(m->attachments[i].name);
    }
    free(m->attachments);
}

/* ── Where things go ── */

/* The calling user's neruna.exe: from "shell\open\command" of Neruna's registration ("C:\…\neruna.exe"). */
static int find_neruna(wchar_t *exe, DWORD size)
{
    wchar_t command[MAX_PATH * 2];
    DWORD bytes = sizeof(command);
    if (RegGetValueW(HKEY_CURRENT_USER, L"Software\\Clients\\Mail\\Neruna\\shell\\open\\command", NULL,
                     RRF_RT_REG_SZ, NULL, command, &bytes) == ERROR_SUCCESS) {
        const wchar_t *start = command;
        const wchar_t *end;
        if (*start == L'"') {
            start++;
            end = wcschr(start, L'"');
        } else {
            end = wcschr(start, L' ');
        }
        size_t n = end ? (size_t)(end - start) : wcslen(start);
        if (n > 0 && n < size) {
            memcpy(exe, start, n * sizeof(wchar_t));
            exe[n] = 0;
            if (GetFileAttributesW(exe) != INVALID_FILE_ATTRIBUTES) {
                return 1;
            }
        }
    }

    /* Not registered (yet): where the installer puts it. */
    DWORD n = ExpandEnvironmentStringsW(L"%LOCALAPPDATA%\\Neruna\\current\\neruna.exe", exe, size);
    return n > 0 && n <= size && GetFileAttributesW(exe) != INVALID_FILE_ATTRIBUTES;
}

/* %TEMP%\Neruna-MAPI\<unique>\ – created; the trailing backslash included. */
static int make_folder(wchar_t *folder, DWORD size)
{
    wchar_t temp[MAX_PATH];
    DWORD n = GetTempPathW(MAX_PATH, temp);
    if (n == 0 || n >= MAX_PATH) {
        return 0;
    }
    wchar_t base[MAX_PATH];
    if (!format(base, MAX_PATH, L"%lsNeruna-MAPI", temp)) {
        return 0;
    }
    CreateDirectoryW(base, NULL);
    for (int attempt = 0; attempt < 20; attempt++) {
        if (!format(folder, size, L"%ls\\%lu-%lu-%d\\", base, (unsigned long)GetCurrentProcessId(), (unsigned long)GetTickCount(), attempt)) {
            return 0;
        }
        if (CreateDirectoryW(folder, NULL)) {
            return 1;
        }
    }
    return 0;
}

/* A file name without path or characters Windows does not allow. */
static void clean_name(wchar_t *name)
{
    for (wchar_t *c = name; *c; c++) {
        if (*c < 0x20 || wcschr(L"\\/:*?\"<>|", *c)) {
            *c = L'_';
        }
    }
}

static const wchar_t *base_name(const wchar_t *path)
{
    const wchar_t *slash = wcsrchr(path, L'\\');
    const wchar_t *other = wcsrchr(path, L'/');
    if (other > slash) {
        slash = other;
    }
    return slash ? slash + 1 : path;
}

/* "SMTP:anna@example.com" → "anna@example.com". */
static const wchar_t *without_smtp(const wchar_t *address)
{
    if (address && _wcsnicmp(address, L"SMTP:", 5) == 0) {
        return address + 5;
    }
    return address;
}

/* Copies the attachments, writes the .nerunamapi file and starts neruna.exe with it. */
static ULONG hand_to_neruna(Mail *m)
{
    wchar_t exe[MAX_PATH];
    if (!find_neruna(exe, MAX_PATH)) {
        return MAPI_E_FAILURE;
    }

    wchar_t folder[MAX_PATH];
    if (!make_folder(folder, MAX_PATH)) {
        return MAPI_E_FAILURE;
    }

    Text json = {0};
    append(&json, L"{\"subject\":");
    append_json(&json, m->subject ? m->subject : L"");
    append(&json, L",\"body\":");
    append_json(&json, m->body ? m->body : L"");
    append(&json, m->dialog ? L",\"dialog\":true" : L",\"dialog\":false");

    append(&json, L",\"recipients\":[");
    for (ULONG i = 0; i < m->recipient_count; i++) {
        Recipient *r = &m->recipients[i];
        const wchar_t *kind = r->kind == MAPI_CC ? L"cc" : r->kind == MAPI_BCC ? L"bcc" : L"to";
        append(&json, i ? L",{\"kind\":" : L"{\"kind\":");
        append_json(&json, kind);
        append(&json, L",\"name\":");
        append_json(&json, r->name ? r->name : L"");
        append(&json, L",\"address\":");
        append_json(&json, without_smtp(r->address) ? without_smtp(r->address) : L"");
        append(&json, L"}");
    }
    append(&json, L"]");

    /* Each attachment in a folder of its own: two files of the same name stay apart. */
    append(&json, L",\"attachments\":[");
    ULONG copied = 0;
    for (ULONG i = 0; i < m->attachment_count; i++) {
        Attachment *a = &m->attachments[i];
        if (!a->path || !*a->path) {
            continue;
        }
        if (GetFileAttributesW(a->path) == INVALID_FILE_ATTRIBUTES) {
            free(json.text);
            return MAPI_E_ATTACHMENT_NOT_FOUND;
        }

        wchar_t name[MAX_PATH];
        const wchar_t *shown = a->name && *a->name ? base_name(a->name) : base_name(a->path);
        lstrcpynW(name, shown, MAX_PATH);
        clean_name(name);

        wchar_t target[MAX_PATH * 2];
        format(target, MAX_PATH * 2, L"%ls%lu", folder, (unsigned long)i);
        CreateDirectoryW(target, NULL);
        if (wcslen(target) + 1 + wcslen(name) >= MAX_PATH) {
            free(json.text);
            return MAPI_E_ATTACHMENT_OPEN_FAILURE;
        }
        lstrcatW(target, L"\\");
        lstrcatW(target, name);
        if (!CopyFileW(a->path, target, FALSE)) {
            free(json.text);
            return MAPI_E_ATTACHMENT_OPEN_FAILURE;
        }

        append(&json, copied++ ? L",{\"path\":" : L"{\"path\":");
        append_json(&json, target);
        append(&json, L",\"name\":");
        append_json(&json, name);
        append(&json, L"}");
    }
    append(&json, L"]}");

    if (json.failed || !json.text) {
        free(json.text);
        return MAPI_E_INSUFFICIENT_MEMORY;
    }

    wchar_t file[MAX_PATH];
    if (!format(file, MAX_PATH, L"%lsmail.nerunamapi", folder)) {
        free(json.text);
        return MAPI_E_FAILURE;
    }
    int bytes = WideCharToMultiByte(CP_UTF8, 0, json.text, (int)json.length, NULL, 0, NULL, NULL);
    char *utf8 = bytes > 0 ? (char *)malloc((size_t)bytes) : NULL;
    if (!utf8) {
        free(json.text);
        return MAPI_E_INSUFFICIENT_MEMORY;
    }
    WideCharToMultiByte(CP_UTF8, 0, json.text, (int)json.length, utf8, bytes, NULL, NULL);
    free(json.text);

    HANDLE handle = CreateFileW(file, GENERIC_WRITE, 0, NULL, CREATE_NEW, FILE_ATTRIBUTE_NORMAL, NULL);
    DWORD written = 0;
    BOOL ok = handle != INVALID_HANDLE_VALUE && WriteFile(handle, utf8, (DWORD)bytes, &written, NULL) && written == (DWORD)bytes;
    if (handle != INVALID_HANDLE_VALUE) {
        CloseHandle(handle);
    }
    free(utf8);
    if (!ok) {
        return MAPI_E_FAILURE;
    }

    /* The calling program is in front: Neruna may come to the front too. */
    AllowSetForegroundWindow(ASFW_ANY);
    wchar_t command[MAX_PATH * 3];
    if (!format(command, MAX_PATH * 3, L"\"%ls\" \"%ls\"", exe, file)) {
        return MAPI_E_FAILURE;
    }
    STARTUPINFOW startup = {0};
    startup.cb = sizeof(startup);
    PROCESS_INFORMATION process = {0};
    if (!CreateProcessW(exe, command, NULL, NULL, FALSE, 0, NULL, NULL, &startup, &process)) {
        return MAPI_E_FAILURE;
    }
    CloseHandle(process.hThread);
    CloseHandle(process.hProcess);
    return SUCCESS_SUCCESS;
}

/* ── Simple MAPI ── */

EXPORT ULONG WINAPI MAPILogon(ULONG_PTR ulUIParam, LPSTR lpszProfileName, LPSTR lpszPassword, ULONG flFlags,
                              ULONG ulReserved, LHANDLE *lplhSession)
{
    trace("MAPILogon", flFlags);
    (void)ulUIParam; (void)lpszProfileName; (void)lpszPassword; (void)flFlags; (void)ulReserved;
    if (lplhSession) {
        *lplhSession = 1;
    }
    return SUCCESS_SUCCESS;
}

EXPORT ULONG WINAPI MAPILogoff(LHANDLE lhSession, ULONG_PTR ulUIParam, ULONG flFlags, ULONG ulReserved)
{
    trace("MAPILogoff", flFlags);
    (void)lhSession; (void)ulUIParam; (void)flFlags; (void)ulReserved;
    return SUCCESS_SUCCESS;
}

EXPORT ULONG WINAPI MAPISendMailW(LHANDLE lhSession, ULONG_PTR ulUIParam, MapiMessageW *lpMessage, ULONG flFlags,
                                  ULONG ulReserved)
{
    trace("MAPISendMailW", flFlags);
    (void)lhSession; (void)ulUIParam; (void)ulReserved;
    if (!lpMessage) {
        return MAPI_E_FAILURE;
    }

    Mail m = {0};
    m.subject = copy_w(lpMessage->lpszSubject);
    m.body = copy_w(lpMessage->lpszNoteText);
    m.dialog = (flFlags & MAPI_DIALOG) != 0;
    m.recipients = (Recipient *)calloc(lpMessage->nRecipCount + 1, sizeof(Recipient));
    m.attachments = (Attachment *)calloc(lpMessage->nFileCount + 1, sizeof(Attachment));
    if (!m.recipients || !m.attachments) {
        free_mail(&m);
        return MAPI_E_INSUFFICIENT_MEMORY;
    }
    for (ULONG i = 0; i < lpMessage->nRecipCount && lpMessage->lpRecips; i++) {
        MapiRecipDescW *r = &lpMessage->lpRecips[i];
        if (r->ulRecipClass < MAPI_TO || r->ulRecipClass > MAPI_BCC) {
            continue; /* the originator */
        }
        Recipient *to = &m.recipients[m.recipient_count++];
        to->kind = r->ulRecipClass;
        to->name = copy_w(r->lpszName);
        to->address = copy_w(r->lpszAddress);
    }
    for (ULONG i = 0; i < lpMessage->nFileCount && lpMessage->lpFiles; i++) {
        Attachment *a = &m.attachments[m.attachment_count++];
        a->path = copy_w(lpMessage->lpFiles[i].lpszPathName);
        a->name = copy_w(lpMessage->lpFiles[i].lpszFileName);
    }

    ULONG result = hand_to_neruna(&m);
    free_mail(&m);
    trace("-> result", result);
    return result;
}

EXPORT ULONG WINAPI MAPISendMail(LHANDLE lhSession, ULONG_PTR ulUIParam, MapiMessageA *lpMessage, ULONG flFlags,
                                 ULONG ulReserved)
{
    trace("MAPISendMail", flFlags);
    (void)lhSession; (void)ulUIParam; (void)ulReserved;
    if (!lpMessage) {
        return MAPI_E_FAILURE;
    }

    Mail m = {0};
    m.subject = from_ansi(lpMessage->lpszSubject);
    m.body = from_ansi(lpMessage->lpszNoteText);
    m.dialog = (flFlags & MAPI_DIALOG) != 0;
    m.recipients = (Recipient *)calloc(lpMessage->nRecipCount + 1, sizeof(Recipient));
    m.attachments = (Attachment *)calloc(lpMessage->nFileCount + 1, sizeof(Attachment));
    if (!m.recipients || !m.attachments) {
        free_mail(&m);
        return MAPI_E_INSUFFICIENT_MEMORY;
    }
    for (ULONG i = 0; i < lpMessage->nRecipCount && lpMessage->lpRecips; i++) {
        MapiRecipDescA *r = &lpMessage->lpRecips[i];
        if (r->ulRecipClass < MAPI_TO || r->ulRecipClass > MAPI_BCC) {
            continue;
        }
        Recipient *to = &m.recipients[m.recipient_count++];
        to->kind = r->ulRecipClass;
        to->name = from_ansi(r->lpszName);
        to->address = from_ansi(r->lpszAddress);
    }
    for (ULONG i = 0; i < lpMessage->nFileCount && lpMessage->lpFiles; i++) {
        Attachment *a = &m.attachments[m.attachment_count++];
        a->path = from_ansi(lpMessage->lpFiles[i].lpszPathName);
        a->name = from_ansi(lpMessage->lpFiles[i].lpszFileName);
    }

    ULONG result = hand_to_neruna(&m);
    free_mail(&m);
    trace("-> result", result);
    return result;
}

/* "Senden an": files separated by lpszDelimChar, no recipients. */
EXPORT ULONG WINAPI MAPISendDocuments(ULONG_PTR ulUIParam, LPSTR lpszDelimChar, LPSTR lpszFilePaths,
                                      LPSTR lpszFileNames, ULONG ulReserved)
{
    trace("MAPISendDocuments", 0);
    (void)ulUIParam; (void)lpszFileNames; (void)ulReserved;
    wchar_t *paths = from_ansi(lpszFilePaths);
    if (!paths) {
        return MAPI_E_FAILURE;
    }
    wchar_t delimiter = lpszDelimChar && *lpszDelimChar ? (wchar_t)(unsigned char)*lpszDelimChar : L';';

    ULONG count = 1;
    for (wchar_t *c = paths; *c; c++) {
        count += *c == delimiter;
    }
    Mail m = {0};
    m.dialog = 1;
    m.attachments = (Attachment *)calloc(count + 1, sizeof(Attachment));
    if (!m.attachments) {
        free(paths);
        return MAPI_E_INSUFFICIENT_MEMORY;
    }
    wchar_t *start = paths;
    for (wchar_t *c = paths;; c++) {
        if (*c == delimiter || *c == 0) {
            int end = *c == 0;
            *c = 0;
            if (*start) {
                m.attachments[m.attachment_count++].path = copy_w(start);
            }
            if (end) {
                break;
            }
            start = c + 1;
        }
    }
    free(paths);

    ULONG result = hand_to_neruna(&m);
    free_mail(&m);
    trace("-> result", result);
    return result;
}

/* An address typed in the calling program: taken as it is (Neruna checks it when sending). */
EXPORT ULONG WINAPI MAPIResolveName(LHANDLE lhSession, ULONG_PTR ulUIParam, LPSTR lpszName, ULONG flFlags,
                                    ULONG ulReserved, MapiRecipDescA **lppRecip)
{
    trace("MAPIResolveName", flFlags);
    (void)lhSession; (void)ulUIParam; (void)flFlags; (void)ulReserved;
    if (!lpszName || !*lpszName || !lppRecip) {
        return MAPI_E_UNKNOWN_RECIPIENT;
    }
    const char *address = _strnicmp(lpszName, "SMTP:", 5) == 0 ? lpszName + 5 : lpszName;
    size_t name_length = strlen(lpszName) + 1;
    size_t address_length = strlen(address) + 6;
    /* One block (freed with MAPIFreeBuffer): the description, then the two texts. */
    MapiRecipDescA *recipient = (MapiRecipDescA *)HeapAlloc(GetProcessHeap(), HEAP_ZERO_MEMORY,
                                                            sizeof(MapiRecipDescA) + name_length + address_length);
    if (!recipient) {
        return MAPI_E_INSUFFICIENT_MEMORY;
    }
    char *name = (char *)(recipient + 1);
    char *smtp = name + name_length;
    memcpy(name, lpszName, name_length);
    memcpy(smtp, "SMTP:", 5);
    memcpy(smtp + 5, address, address_length - 5);
    recipient->ulRecipClass = MAPI_TO;
    recipient->lpszName = name;
    recipient->lpszAddress = smtp;
    *lppRecip = recipient;
    return SUCCESS_SUCCESS;
}

EXPORT ULONG WINAPI MAPIFreeBuffer(LPVOID pv)
{
    if (pv) {
        HeapFree(GetProcessHeap(), 0, pv);
    }
    return SUCCESS_SUCCESS;
}

/* Reading the mailbox and the address book is not offered: Neruna keeps its mail to itself. */
EXPORT ULONG WINAPI MAPIFindNext(LHANDLE a, ULONG_PTR b, LPSTR c, LPSTR d, ULONG e, ULONG f, LPSTR g)
{
    trace("MAPIFindNext", 0);
    (void)a; (void)b; (void)c; (void)d; (void)e; (void)f; (void)g;
    return MAPI_E_NOT_SUPPORTED;
}

EXPORT ULONG WINAPI MAPIReadMail(LHANDLE a, ULONG_PTR b, LPSTR c, ULONG d, ULONG e, MapiMessageA **f)
{
    trace("MAPIReadMail", 0);
    (void)a; (void)b; (void)c; (void)d; (void)e; (void)f;
    return MAPI_E_NOT_SUPPORTED;
}

EXPORT ULONG WINAPI MAPISaveMail(LHANDLE a, ULONG_PTR b, MapiMessageA *c, ULONG d, ULONG e, LPSTR f)
{
    trace("MAPISaveMail", 0);
    (void)a; (void)b; (void)c; (void)d; (void)e; (void)f;
    return MAPI_E_NOT_SUPPORTED;
}

EXPORT ULONG WINAPI MAPIDeleteMail(LHANDLE a, ULONG_PTR b, LPSTR c, ULONG d, ULONG e)
{
    trace("MAPIDeleteMail", 0);
    (void)a; (void)b; (void)c; (void)d; (void)e;
    return MAPI_E_NOT_SUPPORTED;
}

EXPORT ULONG WINAPI MAPIAddress(LHANDLE a, ULONG_PTR b, LPSTR c, ULONG d, LPSTR e, ULONG f, MapiRecipDescA *g,
                                ULONG h, ULONG i, ULONG *j, MapiRecipDescA **k)
{
    trace("MAPIAddress", 0);
    (void)a; (void)b; (void)c; (void)d; (void)e; (void)f; (void)g; (void)h; (void)i; (void)j; (void)k;
    return MAPI_E_NOT_SUPPORTED;
}

EXPORT ULONG WINAPI MAPIDetails(LHANDLE a, ULONG_PTR b, MapiRecipDescA *c, ULONG d, ULONG e)
{
    trace("MAPIDetails", 0);
    (void)a; (void)b; (void)c; (void)d; (void)e;
    return MAPI_E_NOT_SUPPORTED;
}
