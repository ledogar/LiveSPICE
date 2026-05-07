CC ?= gcc
CFLAGS ?= -O3 -fPIC -Wall -Wextra -Werror
LDFLAGS ?= -shared
BUNDLE := build/LiveSPICE-MXR-Phase90.lv2
PLUGIN := $(BUNDLE)/livespice_mxr_phase90.so
TTL := $(BUNDLE)/manifest.ttl $(BUNDLE)/livespice_mxr_phase90.ttl
PREFIX ?= $(HOME)/.lv2

.PHONY: all install clean test

all: $(PLUGIN) $(TTL)

$(BUNDLE):
	mkdir -p $(BUNDLE)

$(PLUGIN): livespice_mxr_phase90.c | $(BUNDLE)
	$(CC) $(CFLAGS) $< $(LDFLAGS) -lm -o $@

$(BUNDLE)/manifest.ttl: manifest.ttl | $(BUNDLE)
	cp $< $@

$(BUNDLE)/livespice_mxr_phase90.ttl: livespice_mxr_phase90.ttl | $(BUNDLE)
	cp $< $@

install: all
	mkdir -p $(PREFIX)
	cp -r $(BUNDLE) $(PREFIX)/

test: all
	LV2_PATH=$(CURDIR)/build lv2ls | grep -F https://livespice.org/plugins/mxr-phase90

clean:
	rm -rf build
