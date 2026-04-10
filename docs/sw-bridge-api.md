## Macro conventions

All LLM-generated macros are SWBasic (.swb) plain-text files stored in /macros/.

Every macro file must follow this structure:

```vb
' @name        Human-readable macro name
' @description What this macro does and any preconditions.
'              Continuation lines are indented and have no tag.
' @author      nodestar ([model name])
' @created     yyyy-MM-dd
Sub main()
    Dim swApp As Object
    Set swApp = Application.SldWorks
    ' ... macro body
End Sub
```

The entry point is always `main` in a module named `MainModule`.
RunMacroTool hard-codes both — the LLM must not deviate from this convention.

Example of what ListMacrosTool would return for a macros repository that stores two macros :

```txt
Found 2 macro(s):

File:        create_bracket.swb
Name:        Create Mounting Bracket
Description: Creates a 50x30x10mm aluminium bracket with a 6mm center hole.
             Requires an open part document with default planes available.
Author:      nodestar
Created:     2025-01-15
Modified:    2025-01-15 14:32:07

File:        export_step.swb
Name:        Export Active Part to STEP
Description: Exports the currently active part document as a STEP AP214 file
             to the same directory as the source file.
Author:      nodestar
Created:     2025-01-14
Modified:    2025-01-14 09:11:43
```
