; Custom NSIS hooks for Esprit Seeder
; Cleans up Task Scheduler tasks and deep link protocol on uninstall

!macro NSIS_HOOK_POSTUNINSTALL
  ; Delete Task Scheduler tasks (silently, /f = no confirmation)
  ; These may or may not exist depending on user configuration
  nsExec::ExecToLog 'schtasks /delete /tn "Esprit-Seeder" /f'
  nsExec::ExecToLog 'schtasks /delete /tn "Esprit-Seeder-Secondary" /f'
  nsExec::ExecToLog 'schtasks /delete /tn "Esprit-Seeder-2" /f'

  ; Remove deep link protocol registry key (espritseeder://)
  DeleteRegKey HKCU "Software\Classes\espritseeder"
!macroend
