# Публикация на GitHub

Ниже — самый простой путь. Git и GitHub CLI в системе могут быть не установлены;
поставь **Git for Windows** и **GitHub Desktop** (или `gh`).

## 1. Репозиторий с исходниками

В папке `CHZM FRAME UNLOCK GitHub`:

```powershell
git init
git add .
git commit -m "CHZM Frame Unlock alpha"
git branch -M main
git remote add origin https://github.com/<USER>/CHZM-FRAME-UNLOCK.git
git push -u origin main
```

Что попадёт в репозиторий:

- `.cs`, `.xaml`, `.csproj`, `app.manifest`, `app.ico`
- `Tests/`
- `README.md`, `LICENSE`, `THIRD_PARTY_NOTICES.md`
- `.github/workflows/build.yml`
- `docs/`, `scripts/`

Что **не** попадёт (и не должно):

- `DLSS/`, `ДОПОЛНЕНИЕ/` — чужие бинарники;
- `bin/`, `obj/`, `.build/`;
- `CHZMFRAMEUNLOCK1.exe`, `*.dll`, `*.pdb`;
- `chzm-settings.json`, `game-library.json`, логи;
- установщик и portable ZIP.

## 2. Проверка сборки

```powershell
dotnet build CHZMFRAMEUNLOCK1.csproj -c Release
```

CI-workflow `.github/workflows/build.yml` делает то же на `windows-latest`.

## 3. Релиз

Собери portable-папку и установщик локально:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\package-release.ps1
```

Затем на GitHub:

1. **Releases → Draft a new release**.
2. Tag: `v1.0.0-alpha`.
3. Прикрепи:
   - `CHZM FRAME UNLOCK Setup.exe` (~780 МБ);
   - `CHZM FRAME UNLOCK portable.zip` (~650 МБ).
4. В описании укажи:
   - ALPHA BUILD;
   - требуются пакеты `DLSS`/`ДОПОЛНЕНИЕ`;
   - античит-предупреждение;
   - контрольные суммы SHA-256.

GitHub Releases принимает файлы до 2 ГБ. Git-репозиторий — только исходники:
1 ГБ бинарников в истории сделает клон невыносимым.

## 4. GitHub CLI (если установлен)

```powershell
gh repo create CHZM-FRAME-UNLOCK --public --source=. --remote=origin --push
gh release create v1.0.0-alpha "CHZM FRAME UNLOCK Setup.exe" "CHZM FRAME UNLOCK portable.zip" --notes-file RELEASE_NOTES.md
```

## 5. Перед публикацией

- Убедись, что в `chzm-settings.json` нет личных путей (он не коммитится).
- Проверь `THIRD_PARTY_NOTICES.md`.
- Не обещай работу в онлайн-играх с античитом через внутренние прокси.
- Добавь скриншоты интерфейса в `docs/` (по желанию).
