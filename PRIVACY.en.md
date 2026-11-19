<!--
  Replace the placeholders before publishing:
    [Developer Name] → your store publisher name (currently "sunnysky")
    [Contact Email]  → an address that can receive privacy inquiries
    [Effective Date] → e.g. 2026-11-20
  The rest reflects the app's actual behaviour; update it if the features change.
  Chinese version: PRIVACY.md
-->

# 小信阅读 (Xinread) Privacy Policy

**Effective date:** [Effective Date]
**Developer:** [Developer Name]
**Product:** 小信阅读 (Xinread), a Windows application (the "App")

[中文版本](PRIVACY.md)

---

## 1. Overview

The App is a third-party client for WeRead (微信读书) that lets you read books from **your own** WeRead account on a Windows PC.

**We do not operate any servers and we do not collect, upload, or sell your personal data.**
The App does exactly two things: it stores required settings locally on your device, and it forwards your requests directly to WeRead's official servers.

## 2. Information We Collect

### 2.1 What we do NOT collect

The App contains **no** telemetry, analytics, crash reporting, or behavioural tracking, and it bundles no third-party statistics, advertising, or push SDKs. As a result, we have no way of knowing:

- which books you read, or how far you have read
- when or how long you use the App
- your device model, OS version, or IP address

We also do not access your clipboard, location, camera, microphone, contacts, or file system (other than the App's own data folder).

### 2.2 Data stored only on your device

The following files are written locally and are never sent to the developer:

| File | Contents | Purpose |
|---|---|---|
| `credentials.json` | WeRead account ID (vid), access token, refresh token, randomly generated device ID | Keeps you signed in, so you don't have to scan a QR code every time |
| `positions.json` | Which chapter you last read in each book | Resumes reading where you left off |
| `readersettings.json` | Font size, speech rate/volume/voice, auto-advance setting | Preserves your reading preferences |

Location (Windows isolates data per application):

```
%LOCALAPPDATA%\Packages\{package identity}\LocalCache\Local\xinread\
```

`credentials.json` is marked as a hidden file. None of these files are encrypted, but they are protected by Windows user-account permissions — only your Windows user can read them.

## 3. Data Transmission

The App requires a network connection to retrieve book content. Requests go **directly to WeRead's servers** and never pass through any server operated by the developer:

| Destination | Domain | What is sent |
|---|---|---|
| WeChat QR sign-in | `open.weixin.qq.com`, `long.open.weixin.qq.com` | QR session identifier, used to poll sign-in status |
| WeRead account & book APIs | `i.weread.qq.com` | Access token, book ID, chapter ID, reading progress |
| WeRead web reader | `weread.qq.com` | Session cookie, chapter content requests |
| Book covers & illustrations | `res.weread.qq.com` | Image URLs (no personal identifiers) |

All connections use HTTPS.

**About the device ID:** On first sign-in the App generates a random device identifier (the prefix `eink334691225` followed by random digits) and sends it to WeRead's servers. It is **not** your hardware serial number, MAC address, or any system-wide identifier that could be used to track you across applications — it is simply a random string required by the server's API. Signing out and in again generates a new one.

**About sign-in:** The App uses QR-code sign-in, which you confirm with WeChat on your phone. **The App never sees or asks for your WeChat or WeRead password.**

## 4. Third-Party Services

The content you read through the App comes from WeRead (Tencent). Your account, the content, and any purchase relationship are governed by **WeRead's own terms of service and privacy policy**, which the App is not a party to:

- WeRead privacy policy: <https://weread.qq.com/privacy>

The App is distributed through the Microsoft Store. Microsoft may collect installation and crash data as part of its own privacy practices — see <https://privacy.microsoft.com/>. The developer cannot obtain personally identifiable information about you from Microsoft.

Other than as described above, we **do not** share, transfer, or sell your information to any third party.

## 5. Your Choices

| What you want | How to do it |
|---|---|
| Sign out and delete tokens | Tap "退出登录" (Sign out) on the bookshelf page; `credentials.json` is deleted |
| Clear reading progress | Delete `positions.json` |
| Reset reader settings | Delete `readersettings.json`, or simply adjust the font size in the reader |
| Remove all local data | Uninstall the App; Windows removes its data folder |

After signing out you can sign in again at any time by scanning a QR code with WeChat.

## 6. Children's Privacy

The App is not directed at children under 13 and does not knowingly collect personal information from them. Whether the App is appropriate for a minor depends on the registration requirements of the WeRead account itself.

## 7. Data Security

Because the App operates no servers, your data never resides in any system controlled by the developer. There is therefore no risk of a developer-side server breach exposing your data. The security of local data depends on Windows account isolation and disk encryption (if you have BitLocker enabled).

Note that the device ID, access tokens, and similar values are still transmitted to WeRead's servers, so their security also depends on that service.

## 8. Changes to This Policy

If a change in the App's features alters how data is handled (for example, adding account sync or data reporting), we will update this document and revise the effective date above. Material changes will be noted in the App's release notes.

## 9. Contact

For any questions about this policy or about data handling, contact:

**Email:** [Contact Email]

---

*This App is a third-party client and is not affiliated with, endorsed by, or connected to WeRead.*
