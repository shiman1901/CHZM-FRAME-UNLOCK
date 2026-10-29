# Пакеты модов

Исходники лаунчера не содержат чужих бинарников. Чтобы приложение увидело
режимы, рядом с `CHZM FRAME UNLOCK.exe` должны лежать две папки:

```
DLSS\
  version.dll
  dlssg_sm86.ini
  dlssg_sm86\
    runtime\nvngx_dlssg.dll
    backend\sm86_backend.dll
    kernels\fg_kernels.dll
    sr\nvngx_dlss.dll
    sr\sr_kernels.dll
  alternatives\
    version.dll, winmm.dll, dinput8.dll, dxgi.dll, dbghelp.dll, d3d12.dll

ДОПОЛНЕНИЕ\
  rtx-encore\
    rtx-encore.dll
    alternative-proxies\
      version.dll, winmm.dll, dxgi.dll, dinput8.dll, rtx-encore.asi

  smooth-motion\
    sm86.exe
    SmoothMotionSM86.exe
    rtx30\version.dll          (RTXMFG SmoothMotion-next, 1 064 448 байт)
    assets\

  live-flow\
    native\
      runtime\python.exe
      screen\controller.py
      nvngx.dll
      nvngx.dll_lf-relay.dll
      dlssg_compat\
      supermotion\
      ...
    runtime\                    (создаётся автоматически)
```

## Где брать

- CLASSIC: https://github.com/sdli1995/dlssg_for_sm86
- ENCORE: https://github.com/SilyNoMeta/rtx-encore/releases
- SMOOTH:
  - SmoothMotionSM86: https://github.com/xikarioz/Smooth-Motion-RTX30
  - RTXMFG / Fix37: https://github.com/pipotoufikxyz-lgtm/dlssg_for_sm86-MFG-version/releases
- LIVE FLOW native: DLSS 5 MANAGER (NODIX-TECH), portable native.

## Важно

- Не смешивай runtime/INI от разных версий.
- Папка `ДОПОЛНЕНИЕ` большая (~830 МБ), `DLSS` ~214 МБ.
- Бинарники не коммить в git. Собирай portable ZIP/installer и загружай в
  GitHub Releases.
- Лицензии и запреты на перепаковку — в `THIRD_PARTY_NOTICES.md`.
