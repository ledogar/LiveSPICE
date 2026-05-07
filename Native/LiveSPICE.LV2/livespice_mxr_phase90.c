#include <math.h>
#include <stdint.h>
#include <stdlib.h>
#include <string.h>

#include <lv2/core/lv2.h>

#define LIVESPICE_MXR_PHASE90_URI "https://livespice.org/plugins/mxr-phase90"

#define MIN_RATE_HZ 0.05f
#define MAX_RATE_HZ 8.0f
#define MIN_SWEEP_HZ 180.0f
#define MAX_SWEEP_HZ 1800.0f
#define STAGE_COUNT 4

typedef enum {
    PORT_SPEED = 0,
    PORT_TRIMMER = 1,
    PORT_INPUT = 2,
    PORT_OUTPUT = 3
} PortIndex;

typedef struct {
    const float* speed;
    const float* trimmer;
    const float* input;
    float* output;
    double sample_rate;
    float phase;
    float x1[STAGE_COUNT];
    float y1[STAGE_COUNT];
} LiveSpiceMxrPhase90;

static float clampf(float value, float min, float max)
{
    if (value < min)
        return min;
    if (value > max)
        return max;
    return value;
}

static LV2_Handle instantiate(const LV2_Descriptor* descriptor, double sample_rate, const char* bundle_path, const LV2_Feature* const* features)
{
    (void)descriptor;
    (void)bundle_path;
    (void)features;

    LiveSpiceMxrPhase90* self = (LiveSpiceMxrPhase90*)calloc(1, sizeof(LiveSpiceMxrPhase90));
    if (self != NULL)
        self->sample_rate = sample_rate;
    return (LV2_Handle)self;
}

static void connect_port(LV2_Handle instance, uint32_t port, void* data)
{
    LiveSpiceMxrPhase90* self = (LiveSpiceMxrPhase90*)instance;
    switch ((PortIndex)port) {
    case PORT_SPEED:
        self->speed = (const float*)data;
        break;
    case PORT_TRIMMER:
        self->trimmer = (const float*)data;
        break;
    case PORT_INPUT:
        self->input = (const float*)data;
        break;
    case PORT_OUTPUT:
        self->output = (float*)data;
        break;
    }
}

static void activate(LV2_Handle instance)
{
    LiveSpiceMxrPhase90* self = (LiveSpiceMxrPhase90*)instance;
    self->phase = 0.0f;
    memset(self->x1, 0, sizeof(self->x1));
    memset(self->y1, 0, sizeof(self->y1));
}

static float process_allpass(LiveSpiceMxrPhase90* self, float input, float coefficient, uint32_t stage)
{
    float output = coefficient * input + self->x1[stage] - coefficient * self->y1[stage];
    self->x1[stage] = input;
    self->y1[stage] = output;
    return output;
}

static void run(LV2_Handle instance, uint32_t sample_count)
{
    LiveSpiceMxrPhase90* self = (LiveSpiceMxrPhase90*)instance;
    if (self->input == NULL || self->output == NULL)
        return;

    float speed = self->speed != NULL ? clampf(*self->speed, 0.0f, 1.0f) : 0.5f;
    float trimmer = self->trimmer != NULL ? clampf(*self->trimmer, 0.0f, 1.0f) : 0.5f;
    float rate = MIN_RATE_HZ * powf(MAX_RATE_HZ / MIN_RATE_HZ, speed);
    float depth = 0.25f + trimmer * 0.75f;
    float mix = 0.35f + trimmer * 0.45f;
    float phase_increment = rate / (float)self->sample_rate;

    for (uint32_t sample = 0; sample < sample_count; sample++) {
        float lfo = 0.5f + 0.5f * sinf(2.0f * (float)M_PI * self->phase);
        float sweep = MIN_SWEEP_HZ + (MAX_SWEEP_HZ - MIN_SWEEP_HZ) * lfo * depth;
        float tangent = tanf((float)M_PI * sweep / (float)self->sample_rate);
        float coefficient = (1.0f - tangent) / (1.0f + tangent);

        float wet = self->input[sample];
        for (uint32_t stage = 0; stage < STAGE_COUNT; stage++)
            wet = process_allpass(self, wet, coefficient, stage);

        self->output[sample] = self->input[sample] * (1.0f - mix) + wet * mix;

        self->phase += phase_increment;
        if (self->phase >= 1.0f)
            self->phase -= floorf(self->phase);
    }
}

static void deactivate(LV2_Handle instance)
{
    (void)instance;
}

static void cleanup(LV2_Handle instance)
{
    free(instance);
}

static const void* extension_data(const char* uri)
{
    (void)uri;
    return NULL;
}

static const LV2_Descriptor descriptor = {
    LIVESPICE_MXR_PHASE90_URI,
    instantiate,
    connect_port,
    activate,
    run,
    deactivate,
    cleanup,
    extension_data
};

LV2_SYMBOL_EXPORT const LV2_Descriptor* lv2_descriptor(uint32_t index)
{
    return index == 0 ? &descriptor : NULL;
}
