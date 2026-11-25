#ifndef CAIYU_WEB_SERVER_H
#define CAIYU_WEB_SERVER_H

#include <cJSON.h>
#include <esp_http_server.h>

#include <cstddef>
#include <cstdint>
#include <mutex>
#include <string>

#include "device_state.h"

class CaiyuDisplay;
class StripController;

namespace caiyu {
class Actionments;
}

class CaiyuWebServer {
public:
    CaiyuWebServer(CaiyuDisplay* display, StripController* strip, caiyu::Actionments* actionments,
                   int port = 80);
    ~CaiyuWebServer();

    bool Start();
    void Stop();

    bool started() const { return server_ != nullptr; }
    int port() const { return port_; }

    bool HasClients() const;

    void SyncChatState();

    /**
     * 把一帧姿态快照推给"订阅了遥测"的客户端（Unity 数字孪生用）。
     * 由板卡守护任务按固定节拍调用；没有订阅者时立刻返回，开销只是一次函数调用。
     */
    void PushTelemetry();

private:
    static constexpr int kMaxSockets = 8;
    static constexpr size_t kMaxMessageLen = 2048;
    static constexpr int kDefaultTelemetryHz = 20;
    static constexpr int kMaxTelemetryHz = 30;

    // 每个 WS 客户端单独记一个订阅位：手机网页不订阅，就不会被 20 Hz 快照刷屏。
    struct Client {
        int fd = 0;
        bool telemetry = false;
    };

    static esp_err_t RootHandler(httpd_req_t* req);
    static esp_err_t StyleHandler(httpd_req_t* req);
    static esp_err_t ScriptHandler(httpd_req_t* req);
    static esp_err_t FaviconHandler(httpd_req_t* req);
    static esp_err_t WsHandler(httpd_req_t* req);

    static esp_err_t SendEmbedded(httpd_req_t* req, const char* content_type, const uint8_t* begin,
                                  const uint8_t* end);

    esp_err_t OnWebSocket(httpd_req_t* req);
    void HandleMessage(httpd_req_t* req, const char* data, size_t len);
    void HandleEmotion(httpd_req_t* req, const cJSON* root);
    void HandleLamp(httpd_req_t* req, const cJSON* root);
    void SendLampState(httpd_req_t* req);
    void HandleAction(httpd_req_t* req, const cJSON* root);
    void SendActionState(httpd_req_t* req);
    void HandleDrive(httpd_req_t* req, const cJSON* root);
    void SendDriveState(httpd_req_t* req);
    void HandleChat(httpd_req_t* req, const cJSON* root);
    void SendChatState(httpd_req_t* req);
    void BroadcastChatState();
    void HandleTrim(httpd_req_t* req, const cJSON* root);
    void SendTrimState(httpd_req_t* req);
    void HandleSpeed(httpd_req_t* req, const cJSON* root);
    void SendSpeedState(httpd_req_t* req);
    void HandleTelemetry(httpd_req_t* req, const cJSON* root);
    void BroadcastJson(const std::string& message);

    // 姿态快照：一次遍历拼出完整 JSON（不走 cJSON，省堆也省 CPU）。
    std::string BuildTelemetryJson() const;
    void BroadcastTelemetry(const std::string& message);
    bool HasTelemetryClients() const;
    void SetClientTelemetry(int fd, bool enabled);

    static void SendText(httpd_req_t* req, const std::string& message);
    void AddClient(int fd);
    void RemoveClient(int fd);

    CaiyuDisplay* display_ = nullptr;
    StripController* strip_ = nullptr;
    caiyu::Actionments* actionments_ = nullptr;
    int port_ = 80;
    httpd_handle_t server_ = nullptr;

    mutable std::mutex client_mutex_;
    Client clients_[kMaxSockets] = {};
    int telemetry_hz_ = kDefaultTelemetryHz;
    uint32_t last_telemetry_ms_ = 0;

    // WS 帧缓冲提到成员里，避免 2 KB 数组压在 httpd 任务的栈上。
    uint8_t rx_buffer_[kMaxMessageLen + 1] = {};

    DeviceState last_chat_state_ = kDeviceStateUnknown;

    static CaiyuWebServer* instance_;
};

#endif
