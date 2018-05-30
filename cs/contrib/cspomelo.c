/**
 * Copyright (c) 2014-2015 NetEase, Inc. and other Pomelo contributors
 * MIT Licensed.
 */

#include <assert.h>
#include <string.h>
#include <stdlib.h>
#include <stdio.h>
#include <stdarg.h>

#include <pomelo.h>

#define __UNITYEDITOR__

#ifdef __UNITYEDITOR__
#include <time.h>
#endif

#ifdef _WIN32
#define CS_POMELO_EXPORT __declspec(dllexport)
#else
#define CS_POMELO_EXPORT
#endif

#if defined(__ANDROID__)

#include <android/log.h>
#define LOGV(...) __android_log_print(ANDROID_LOG_VERBOSE, "cspomelo", __VA_ARGS__)
#define LOGD(...) __android_log_print(ANDROID_LOG_DEBUG ,"cspomelo", __VA_ARGS__)
#define LOGI(...) __android_log_print(ANDROID_LOG_INFO  ,"cspomelo", __VA_ARGS__)
#define LOGW(...) __android_log_print(ANDROID_LOG_WARN  ,"cspomelo", __VA_ARGS__)
#define LOGE(...) __android_log_print(ANDROID_LOG_ERROR ,"cspomelo", __VA_ARGS__)

void android_log(int level, const char* msg, ...)
{
    char buf[256];
    va_list va;

    if (level < 0) {
        return;
    }

    va_start(va, msg);
    vsnprintf(buf, 256, msg, va);
    va_end(va);

    switch(level) {
        case PC_LOG_DEBUG:
            LOGD("%s", buf);
            break;
        case PC_LOG_INFO:
            LOGI("%s", buf);
            break;
        case PC_LOG_WARN:
            LOGW("%s", buf);
            break;
        case PC_LOG_ERROR:
            LOGE("%s", buf);
            break;
        default:
            LOGV("%s", buf);
    }
}

#endif

#if defined(__UNITYEDITOR__)

static FILE* f = NULL;
void unity_log(int level, const char* msg, ...)
{
    if (!f) {
        return;
    }

    time_t t = time(NULL);
    char buf[32];
    va_list va;
    int n;

    if (level < 0) {
        return;
    }

    n = strftime(buf, 32, "[%Y-%m-%d %H:%M:%S]", localtime(&t));
    fwrite(buf, sizeof(char), n, f);

    switch(level) {
        case PC_LOG_DEBUG:
            fprintf(f, "U[DEBUG] ");
            break;
        case PC_LOG_INFO:
            fprintf(f, "U[INFO] ");
            break;
        case PC_LOG_WARN:
            fprintf(f, "U[WARN] ");
            break;
        case PC_LOG_ERROR:
            fprintf(f, "U[ERROR] ");
            break;
    }

    va_start(va, msg);
    vfprintf(f, msg, va);
    va_end(va);

    fprintf(f, "\n");
}
#endif

CS_POMELO_EXPORT int init_log(const char* path)
{
#if defined(__UNITYEDITOR__)
    f = fopen(path, "w");
    if (!f) {
        return -1;
    }
#endif
    return 0;
}

CS_POMELO_EXPORT void native_log(const char* msg)
{
    if (!msg || strlen(msg) == 0) {
        return;
    }
//    pc_lib_log(PC_LOG_DEBUG, msg);
}

typedef void (*request_handler)(const char* err, const char* resp);

typedef void (*request_callback)(pc_client_t* client, unsigned int cbid, const char* resp);
typedef void (*request_error_callback)(pc_client_t* client, unsigned int cbid, pc_error_t error);


typedef struct {
    char* (* read) ();
    int   (* write)(char* data);
} lc_callback_t;

typedef struct {
    unsigned int cbid;
    request_callback cb;
    request_error_callback error_cb;
} request_cb_t;

static int local_storage_cb(pc_local_storage_op_t op, char* data, size_t* len, void* ex_data)
{
    lc_callback_t* lc_cb = (lc_callback_t* )ex_data;
    char* res = NULL;

    if (op == PC_LOCAL_STORAGE_OP_WRITE) {
        return lc_cb->write(data);
    } else {
        res = lc_cb->read();
        if (!res) {
            return -1;
        }

        *len = strlen(res);
        if (*len == 0) {
            return -1;
        }

        if (data) {
            strncpy(data, res, *len);
        }
        return 0;
    }
    // never go to here
    return -1;
}

static void default_request_cb(const pc_request_t* req, const char* resp)
{
    request_cb_t* rp = (request_cb_t*)pc_request_ex_data(req);
    pc_client_t* client = pc_request_client(req);
    assert(rp);
    request_cb_t r = *rp;
    free(rp);

    r.cb(client, r.cbid, resp);
}

static void default_error_cb(const pc_request_t* req, pc_error_t error)
{
    request_cb_t* rp = (request_cb_t*)pc_request_ex_data(req);
    pc_client_t* client = pc_request_client(req);
    assert(rp);
    request_cb_t r = *rp;
    free(rp);
    r.error_cb(client, r.cbid, error);
}

CS_POMELO_EXPORT void lib_init(int log_level, const char* ca_file, const char* ca_path)
{
#if !defined(PC_NO_UV_TLS_TRANS)
    if (ca_file || ca_path) {
        tr_uv_tls_set_ca_file(ca_file, ca_path);
    }
#endif

    pc_lib_set_default_log_level(log_level);
#if defined(__ANDROID__)
    pc_lib_init(android_log, NULL, NULL, NULL, "CSharp Client");
#elif defined(__UNITYEDITOR__)
    pc_lib_init(unity_log, NULL, NULL, NULL,"CSharp Client");
#else
    pc_lib_init(NULL, NULL, NULL, NULL, "CSharp Client");
#endif
}

CS_POMELO_EXPORT pc_client_t* create(int enable_tls, int enable_poll, int enable_reconnect)
{
    pc_client_init_result_t res = {0};
    pc_client_config_t config = PC_CLIENT_CONFIG_DEFAULT;
    if (enable_tls) {
        config.transport_name = PC_TR_NAME_UV_TLS;
    }
    if (enable_poll) {
        config.enable_polling = 1;
    }
    
    config.enable_reconn = enable_reconnect;

    res = pc_client_init(NULL, &config);
    if (res.rc == PC_RC_OK) {
        return res.client;
    }

    return NULL;
}

CS_POMELO_EXPORT void destroy(pc_client_t* client)
{
    lc_callback_t* lc_cb;

#if defined(__UNITYEDITOR__)
    fclose(f);
    f = NULL;
#endif

    if (pc_client_cleanup(client) == PC_RC_OK) {
        lc_cb = (lc_callback_t*)pc_client_config(client)->ls_ex_data;
        if (lc_cb) {
            free(lc_cb);
        }
        free(client);
    }
}

CS_POMELO_EXPORT int request(pc_client_t* client, const char* route, const char* msg,
                             unsigned int cbid, int timeout, request_callback cb, request_error_callback error_cb)
{
    unity_log(PC_LOG_DEBUG, "route: %s msg %s cbid: %d timeout %d ",  route, msg, cbid, timeout);
    request_cb_t* rp = (request_cb_t*)malloc(sizeof(request_cb_t));
    if (!rp) {
        return PC_RC_TIMEOUT;
    }
    rp->cb = cb;
    rp->error_cb = error_cb;
    rp->cbid= cbid;
    return pc_request_with_timeout(client, route, msg, rp, timeout, default_request_cb, default_error_cb);
}

