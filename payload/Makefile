# PS5 Suite Server - Makefile
# Uses the maintained ps5-payload-dev/sdk (2026 release, v0.43+)

PS5_PAYLOAD_SDK ?= /opt/ps5-payload-sdk
PS5_IP          ?= 192.168.0.160

CC  := $(PS5_PAYLOAD_SDK)/bin/prospero-clang
CXX := $(PS5_PAYLOAD_SDK)/bin/prospero-clang++
LD  := $(PS5_PAYLOAD_SDK)/bin/prospero-lld
DEPLOY := $(PS5_PAYLOAD_SDK)/bin/prospero-deploy

ELF := ps5_suite_server.elf
CFLAGS := -Wall -O3

all: $(ELF)

# Static link like the proven game_mounter/working-v6 model — libkernel_sys
# (NOT kernel_web: externs like sceKernelGetCpuTemperature don't exist in the
# runtime web variant and would make the ELF fail to launch).
$(ELF): main.c
	$(CC) $(CFLAGS) -o $@ $^ -lkernel_sys -lSceSystemService -lSceUserService -lSceAppInstUtil -lSceNetCtl -lScePad -lsqlite3
	$(PS5_PAYLOAD_SDK)/bin/llvm-strip $@

# Push the freshly built payload straight to the console (elfldr must be
# running; uses the http:// URI support added in SDK v0.36.1).
deploy: $(ELF)
	$(DEPLOY) http://$(PS5_IP):9021 $(ELF)

clean:
	rm -f $(ELF)

.PHONY: all clean deploy
