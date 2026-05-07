CC ?= gcc
CFLAGS ?= -O3 -fPIC -Wall -Wextra -Werror
LDFLAGS ?= -shared
BUNDLE := build/LiveSPICE.lv2
MXR_PLUGIN := $(BUNDLE)/livespice_mxr_phase90.so
GENERIC_PLUGIN := $(BUNDLE)/livespice_generic.so
TTL := $(BUNDLE)/manifest.ttl $(BUNDLE)/livespice_mxr_phase90.ttl $(BUNDLE)/livespice_generic.ttl
PREFIX ?= $(HOME)/.lv2

.PHONY: all install clean test

all: $(MXR_PLUGIN) $(GENERIC_PLUGIN) $(TTL)

$(BUNDLE):
	mkdir -p $(BUNDLE)

$(MXR_PLUGIN): livespice_mxr_phase90.c | $(BUNDLE)
	$(CC) $(CFLAGS) $< $(LDFLAGS) -lm -o $@

$(GENERIC_PLUGIN): livespice_generic.c | $(BUNDLE)
	$(CC) $(CFLAGS) $< $(LDFLAGS) -lm -o $@

$(BUNDLE)/manifest.ttl: manifest.ttl | $(BUNDLE)
	cp $< $@

$(BUNDLE)/livespice_mxr_phase90.ttl: livespice_mxr_phase90.ttl | $(BUNDLE)
	cp $< $@

$(BUNDLE)/livespice_generic.ttl: livespice_generic.ttl | $(BUNDLE)
	cp $< $@

install: all
	mkdir -p $(PREFIX)
	rm -rf $(PREFIX)/LiveSPICE-MXR-Phase90.lv2
	cp -r $(BUNDLE) $(PREFIX)/

test: all
	LV2_PATH=$(CURDIR)/build lv2ls | grep -F https://livespice.org/plugins/mxr-phase90
	LV2_PATH=$(CURDIR)/build lv2ls | grep -F https://livespice.org/plugins/generic

clean:
	rm -rf build
