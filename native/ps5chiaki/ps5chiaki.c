// SPDX-License-Identifier: LicenseRef-AGPL-3.0-only-OpenSSL
// Flat C API shim over libchiaki for embedding into the PS5 Suite managed app.
// Owns: session, ffmpeg decoder, YUV->BGRA conversion, controller state.
// Exposes only opaque pointers + flat structs so C# P/Invoke stays trivial.

#include <chiaki/session.h>
#include <chiaki/ffmpegdecoder.h>
#include <chiaki/controller.h>
#include <chiaki/log.h>
#include <chiaki/common.h>
#include <chiaki/thread.h>
#include <chiaki/audio.h>
#include <chiaki/audioreceiver.h>
#include <chiaki/opusdecoder.h>

#include <libswscale/swscale.h>
#include <libavutil/pixfmt.h>

#include <stdlib.h>
#include <string.h>

#ifdef _WIN32
#define PC_EXPORT __declspec(dllexport)
#else
#define PC_EXPORT __attribute__((visibility("default")))
#endif

typedef void (*pc_log_cb)(int level, const char *msg, void *user);
typedef void (*pc_event_cb)(int type, int reason, const char *msg, void *user);
typedef void (*pc_audio_cb)(const uint8_t *pcm, int size_bytes, int rate, int channels, void *user);
typedef void (*pc_frame_cb)(void *user);

typedef struct pc_config_t
{
	const char *host;            // utf8, chiaki copies via getaddrinfo during init
	uint8_t regist_key[16];
	uint8_t morning[16];
	uint8_t account_id[8];
	int32_t codec;               // 0 h264 / 1 h265 / 2 h265 hdr
	uint32_t width, height, max_fps, bitrate;
	int32_t auto_downgrade;
} PCConfig;

typedef struct pc_ctx_t
{
	ChiakiSession session;
	ChiakiLog log;
	ChiakiFfmpegDecoder dec;
	int dec_ok;
	struct SwsContext *sws;
	ChiakiControllerState ctrl_state;

	ChiakiOpusDecoder opus_dec;
	int opus_ok;
	int audio_rate, audio_channels;

	pc_event_cb event_cb;  void *event_user;
	pc_audio_cb audio_cb;  void *audio_user;
	pc_frame_cb frame_cb;  void *frame_user;
} PCCtx;

// ---- log --------------------------------------------------------------

static pc_log_cb g_log_cb;
static void *g_log_user;
static ChiakiLog g_log;

static void pc_log_forward(ChiakiLogLevel level, const char *msg, void *user)
{
	(void)user;
	if(g_log_cb)
		g_log_cb((int)level, msg, g_log_user);
}

PC_EXPORT void pc_set_log_cb(pc_log_cb cb, void *user)
{
	g_log_cb = cb;
	g_log_user = user;
}

PC_EXPORT int pc_init(void)
{
	chiaki_log_init(&g_log, CHIAKI_LOG_ALL, pc_log_forward, NULL);
	return (int)chiaki_lib_init();
}

// ---- events ------------------------------------------------------------

static void pc_event_fwd(ChiakiEvent *ev, void *user)
{
	PCCtx *c = user;
	if(!c->event_cb)
		return;
	int type = (int)ev->type;
	int reason = 0;
	const char *msg = NULL;
	if(ev->type == CHIAKI_EVENT_QUIT)
	{
		reason = (int)ev->quit.reason;
		msg = ev->quit.reason_str;
	}
	else if(ev->type == CHIAKI_EVENT_KEYBOARD_OPEN || ev->type == CHIAKI_EVENT_KEYBOARD_TEXT_CHANGE)
	{
		msg = ev->keyboard.text_str;
	}
	else if(ev->type == CHIAKI_EVENT_NICKNAME_RECEIVED)
	{
		msg = ev->server_nickname;
	}
	c->event_cb(type, reason, msg, c->event_user);
}

// ---- audio -------------------------------------------------------------

static void pc_audio_settings(uint8_t channels, uint32_t rate, void *user)
{
	PCCtx *c = user;
	c->audio_channels = channels;
	c->audio_rate = (int)rate;
}

// opus decoder delivers decoded s16 PCM: buf has samples_count frames,
// interleaved across audio_channels.
static void pc_audio_pcm_fwd(int16_t *buf, size_t samples_count, void *user)
{
	PCCtx *c = user;
	if(c->audio_cb)
		c->audio_cb((const uint8_t *)buf,
			(int)(samples_count * (size_t)c->audio_channels * sizeof(int16_t)),
			c->audio_rate, c->audio_channels, c->audio_user);
}

// ---- video -------------------------------------------------------------

static void pc_frame_avail(ChiakiFfmpegDecoder *decoder, void *user)
{
	(void)decoder;
	PCCtx *c = user;
	if(c->frame_cb)
		c->frame_cb(c->frame_user);
}

// ---- session -----------------------------------------------------------

PC_EXPORT void *pc_create(const PCConfig *cfg)
{
	if(!cfg || !cfg->host)
		return NULL;

	PCCtx *c = calloc(1, sizeof(PCCtx));
	if(!c)
		return NULL;
	c->log = g_log;

	ChiakiConnectInfo ci;
	memset(&ci, 0, sizeof(ci));
	ci.ps5 = true;
	ci.host = cfg->host;   // chiaki copies via getaddrinfo during init
	memcpy(ci.regist_key, cfg->regist_key, CHIAKI_SESSION_AUTH_SIZE);
	memcpy(ci.morning, cfg->morning, 0x10);
	memcpy(ci.psn_account_id, cfg->account_id, CHIAKI_PSN_ACCOUNT_ID_SIZE);
	ci.video_profile.width = cfg->width;
	ci.video_profile.height = cfg->height;
	ci.video_profile.max_fps = cfg->max_fps;
	ci.video_profile.bitrate = cfg->bitrate;
	ci.video_profile.codec = (ChiakiCodec)cfg->codec;
	ci.video_profile_auto_downgrade = cfg->auto_downgrade != 0;
	ci.enable_keyboard = true;
	ci.enable_dualsense = true;
	ci.audio_video_disabled = CHIAKI_NONE_DISABLED;
	ci.auto_regist = false;
	ci.packet_loss_max = 0.05;
	ci.enable_idr_on_fec_failure = false;

	if(chiaki_session_init(&c->session, &ci, &c->log) != CHIAKI_ERR_SUCCESS)
	{
		free(c);
		return NULL;
	}

	if(chiaki_ffmpeg_decoder_init(&c->dec, &c->log, (ChiakiCodec)cfg->codec,
		(unsigned int)cfg->max_fps, NULL, NULL, pc_frame_avail, c) == CHIAKI_ERR_SUCCESS)
	{
		c->dec_ok = 1;
		chiaki_session_set_video_sample_cb(&c->session,
			chiaki_ffmpeg_decoder_video_sample_cb, &c->dec);
	}

	c->audio_rate = 48000;
	c->audio_channels = 2;
	chiaki_opus_decoder_init(&c->opus_dec, &c->log);
	c->opus_ok = 1;
	chiaki_opus_decoder_set_cb(&c->opus_dec, pc_audio_settings, pc_audio_pcm_fwd, c);
	ChiakiAudioSink sink;
	chiaki_opus_decoder_get_sink(&c->opus_dec, &sink);
	chiaki_session_set_audio_sink(&c->session, &sink);

	chiaki_session_set_event_cb(&c->session, pc_event_fwd, c);
	chiaki_controller_state_set_idle(&c->ctrl_state);
	return c;
}

PC_EXPORT void pc_set_cbs(void *v, pc_event_cb ev, void *ev_user,
	pc_audio_cb au, void *au_user, pc_frame_cb fr, void *fr_user)
{
	PCCtx *c = v;
	c->event_cb = ev;  c->event_user = ev_user;
	c->audio_cb = au;  c->audio_user = au_user;
	c->frame_cb = fr;  c->frame_user = fr_user;
}

PC_EXPORT int pc_start(void *v)   { return (int)chiaki_session_start(&((PCCtx*)v)->session); }
PC_EXPORT int pc_stop(void *v)    { return (int)chiaki_session_stop(&((PCCtx*)v)->session); }
PC_EXPORT int pc_join(void *v)    { return (int)chiaki_session_join(&((PCCtx*)v)->session); }

PC_EXPORT void pc_free(void *v)
{
	PCCtx *c = v;
	if(!c) return;
	if(c->dec_ok)
		chiaki_ffmpeg_decoder_fini(&c->dec);
	if(c->opus_ok)
		chiaki_opus_decoder_fini(&c->opus_dec);
	if(c->sws)
		sws_freeContext(c->sws);
	chiaki_session_fini(&c->session);
	free(c);
}

// Pull latest decoded frame as BGRA into caller buffer.
// Returns 1 and fills w/h/pts on success, 0 when no frame available.
PC_EXPORT int pc_pull_bgra(void *v, uint8_t *dst, int dst_stride,
	int *out_w, int *out_h, double *out_pts)
{
	PCCtx *c = v;
	if(!c || !c->dec_ok)
		return 0;

	int32_t frames_lost = 0;
	ChiakiFfmpegFrame f = chiaki_ffmpeg_decoder_pull_frame(&c->dec, &frames_lost);
	AVFrame *avf = f.frame;
	if(!avf || !avf->data[0])
		return 0;

	int w = avf->width, h = avf->height;
	struct SwsContext *sws = sws_getCachedContext(c->sws,
		w, h, (enum AVPixelFormat)avf->format,
		w, h, AV_PIX_FMT_BGRA,
		SWS_BILINEAR, NULL, NULL, NULL);
	if(!sws)
	{
		av_frame_free(&avf);
		return 0;
	}
	c->sws = sws;

	uint8_t *dst_planes[4] = { dst, NULL, NULL, NULL };
	int dst_strides[4] = { dst_stride, 0, 0, 0 };
	sws_scale(sws, (const uint8_t * const *)avf->data, avf->linesize,
		0, h, dst_planes, dst_strides);

	if(out_w) *out_w = w;
	if(out_h) *out_h = h;
	if(out_pts) *out_pts = f.pts;
	av_frame_free(&avf);
	return 1;
}

// ---- input ---------------------------------------------------------------

PC_EXPORT void pc_set_controller(void *v, uint32_t buttons,
	int16_t lx, int16_t ly, int16_t rx, int16_t ry, uint8_t l2, uint8_t r2)
{
	PCCtx *c = v;
	c->ctrl_state.buttons = buttons;
	c->ctrl_state.left_x = lx;
	c->ctrl_state.left_y = ly;
	c->ctrl_state.right_x = rx;
	c->ctrl_state.right_y = ry;
	c->ctrl_state.l2_state = l2;
	c->ctrl_state.r2_state = r2;
	chiaki_session_set_controller_state(&c->session, &c->ctrl_state);
}

PC_EXPORT int pc_set_login_pin(void *v, const char *pin)
{
	return (int)chiaki_session_set_login_pin(&((PCCtx*)v)->session,
		(const uint8_t *)pin, pin ? strlen(pin) : 0);
}

PC_EXPORT int pc_go_home(void *v)        { return (int)chiaki_session_go_home(&((PCCtx*)v)->session); }
PC_EXPORT int pc_goto_bed(void *v)       { return (int)chiaki_session_goto_bed(&((PCCtx*)v)->session); }
PC_EXPORT int pc_keyboard_text(void *v, const char *t)  { return (int)chiaki_session_keyboard_set_text(&((PCCtx*)v)->session, t); }
PC_EXPORT int pc_keyboard_accept(void *v) { return (int)chiaki_session_keyboard_accept(&((PCCtx*)v)->session); }
PC_EXPORT int pc_keyboard_reject(void *v) { return (int)chiaki_session_keyboard_reject(&((PCCtx*)v)->session); }
PC_EXPORT int pc_request_idr(void *v)     { return (int)chiaki_session_request_idr(&((PCCtx*)v)->session); }
