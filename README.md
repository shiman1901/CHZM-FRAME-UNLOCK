# CHZM FRAME UNLOCK

**ALPHA BUILD** · Windows 10/11 · .NET 8 · WPF

Лаунчер-менеджер для NVIDIA Frame Generation на RTX 30:
четыре режима, внешний LIVE FLOW для античит-игр и единый монохромный интерфейс.

> Это не готовый «всё включено» архив. Исходники не содержат чужих бинарников
> DLSS / RTX Encore / Smooth Motion / NODIX. Для запуска нужны папки `DLSS\` и
> `ДОПОЛНЕНИЕ\`, собранные по инструкции из `docs/PACKAGES.md`.

---

## Возможности

| Режим | Что делает |
|---|---|
| `CLASSIC` | Патч DLSS-G (`dlssg_for_sm86`) + M-INT8 / FG-Lite, до 6X |
| `ENCORE` | RTX Encore v1.4.5, собственное внутриигровое меню, до 6X |
| `SMOOTH` | Драйверный Smooth Motion NVIDIA для RTX 30, 2X |
| `LIVE FLOW` | Внешний захват DXGI + Optical Flow, не трогает файлы игры |

- Автоскан Steam / Epic / GOG / локальных папок.
- Отдельная папка установки мода — рядом с реальным `.exe` игры.
- LIVE FLOW: прогрев в памяти, мгновенное вкл/выкл, пауза при Alt+Tab.
- Горячие клавиши: `F9` для LIVE FLOW, `Nitro [N]` (долгое удержание), `F8` для меню Encore.
- RTSS-профили для воркеров LIVE FLOW (без двойного счётчика).
- Монохромный FUI-интерфейс, без скруглений и лишних цветов.

---

## Быстрый запуск

1. Скачай **Release**:
   - `CHZM FRAME UNLOCK Setup.exe` — установщик;
   - или `CHZM FRAME UNLOCK portable.zip` — портативная папка.
2. Установи/распакуй.
3. Запусти `CHZM FRAME UNLOCK.exe` от имени администратора.
4. Папки `DLSS` и `ДОПОЛНЕНИЕ` должны лежать рядом с `.exe`.

---

## Сборка из исходников

Нужен [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```powershell
dotnet build CHZMFRAMEUNLOCK1.csproj -c Release
dotnet publish CHZMFRAMEUNLOCK1.csproj -c Release
```

Готовый `CHZMFRAMEUNLOCK1.exe` появится в корне проекта и в
`bin\Release\net8.0-windows\win-x64\publish\`.

### Пакеты модов

Исходники **не включают** чужие бинарники. См. `docs/PACKAGES.md`:

```
DLSS\                 ← CLASSIC (sdli1995/dlssg_for_sm86)
ДОПОЛНЕНИЕ\
  rtx-encore\         ← ENCORE (SilyNoMeta/rtx-encore v1.4.5)
  smooth-motion\      ← SMOOTH (SmoothMotionSM86 / RTXMFG)
  live-flow\native\   ← LIVE FLOW (NODIX-TECH, portable native)
```

---

## Тесты

```powershell
cd Tests\EngineSelfTest
dotnet run -c Release
```

Автотест проверяет движки, конфиги, сканер, античит-детектор, настройки и
LIVE FLOW. Для части проверок нужны пакеты модов рядом с проектом.

---

## GitHub

Пошаговая инструкция — в `docs/RELEASE.md`.
Коротко:

```powershell
git init
git add .
git commit -m "CHZM Frame Unlock alpha"
git branch -M main
git remote add origin https://github.com/<user>/CHZM-FRAME-UNLOCK.git
git push -u origin main
```

Установщик и portable-архив загружай в **Releases**, а не в git:
GitHub плохо переносит репозитории с гигабайтами бинарников.

---

## Лицензии и безопасность

- Код проекта — MIT (`LICENSE`).
- Сторонние моды и рантаймы принадлежат их авторам: см.
  `THIRD_PARTY_NOTICES.md` и `docs/PACKAGES.md`.
- Не используй внутренние прокси в онлайн-играх с античитом. Для них
  предназначен только `LIVE FLOW`.
- Проект не связан с NVIDIA, Intel, NODIX-TECH, Activision или Embark.
