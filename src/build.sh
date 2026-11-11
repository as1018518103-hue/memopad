#!/bin/bash
# build MemoPad.exe against .NET Framework 4.5 reference assemblies
cd /home/claude/memo
A=/usr/lib/mono/4.5-api
mcs -nostdlib -target:winexe -win32icon:app.ico -out:${1:-MemoPad.exe} -r:$A/mscorlib.dll -r:$A/System.dll -r:$A/System.Core.dll -r:$A/System.Drawing.dll -r:$A/System.Windows.Forms.dll -r:$A/System.Runtime.Serialization.dll -r:$A/System.Xml.dll -resource:wks-Regular.ttf.gz,font_Regular -resource:wks-Bold.ttf.gz,font_Bold Core.cs Theme.cs App.cs Views.cs Calc.cs Updater.cs 2>&1 | grep -E "error|warning"
exit 0
