#!/bin/sh
cp -f lcc/bin/Release/lcc.exe* lcc/bin/Release/*.dll lcs/bin/Release/lcs.exe* Release/
/cygdrive/c/Program\ Files/7-Zip/7z.exe a Release/LanCaster-Release.zip Release/lcc.exe* Release/*.dll Release/lcs.exe*