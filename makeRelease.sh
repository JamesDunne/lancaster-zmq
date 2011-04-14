#!/bin/sh
cp -f lcc/bin/Release/lcc.exe* lcc/bin/Release/*.dll lcs/bin/Release/lcs.exe* README LanCaster-Release/
/cygdrive/c/Program\ Files/7-Zip/7z.exe a LanCaster-Release.zip LanCaster-Release/*