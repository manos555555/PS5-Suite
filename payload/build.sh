#!/bin/bash
export PS5_PAYLOAD_SDK=/opt/ps5-payload-sdk
export PATH=/opt/ps5-payload-sdk/bin:/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin
cd "$(dirname "$0")"
make clean
make
