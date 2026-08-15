---
name: Bug report
about: Something did not convert, or the menu did not appear
labels: bug
---

**What happened**


**What you expected**


**File type in, format out**
e.g. HEIC to JPG


**Does "Convert to" appear in the right-click menu?**
- [ ] Yes
- [ ] Only under "Show more options"
- [ ] Not at all

If it is missing, please run this and paste the result — the classic Windows 10
context menu setting hides it entirely:

```
reg query "HKCU\Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}"
```

**Log**
`%USERPROFILE%\.jalyro-convert\logs\host.log` — the last 30 lines.

**Windows version**
`winver`
