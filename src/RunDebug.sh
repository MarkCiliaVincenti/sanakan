#!/bin/bash
rval=1
while ((rval == 1)); do
    dotnet ./bin/Debug/netcoreapp3.1/Sanakan.dll
    rval=$?
    echo "$rval"
    if ((rval == 200))
    then
        make all-update-debug
        rval=1
    fi
sleep 1
done