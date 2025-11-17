#include <iostream>
#include <string>
#include <vector>
#include <unordered_map>
#include <ctime>
#include <sstream>
#include <iomanip>
#include <vendor/nlohmann/json.hpp>
#include <optional>
#include <vendor/cppcodec/base64_rfc4648.hpp>
#include <aws/lambda-runtime/runtime.h>

using json = nlohmann::json;
using namespace cppcodec;
using namespace aws::lambda_runtime;

// Structs
struct Remote {
    std::string IpAddress;
};

struct Local {
    std::string Domain;
    int Port;
};

struct Message {
    std::string Tag;
    Remote remote;
    Local local;
    std::string ReceivedAt;
    std::string Formatter;
    std::string Data;
};

struct InboundPackets {
    std::vector<Message> Messages;
};

struct OutboundPacket {
    std::string GeneratedAt;
    std::string Tag;
    std::string Data;
};

struct Response {
    std::vector<OutboundPacket> Replies;
};

// Helper: get current UTC time as ISO8601
std::string current_utc_iso() {
    std::time_t t = std::time(nullptr);
    std::tm tm{};
#if defined(_WIN32)
    gmtime_s(&tm, &t);
#else
    gmtime_r(&t, &tm);
#endif
    std::ostringstream oss;
    oss << std::put_time(&tm, "%Y-%m-%dT%H:%M:%SZ");
    return oss.str();
}


static invocation_response my_handler(invocation_request const& req)
{
    try
    {
        auto input = req.payload;

        // Parse inbound packets
        auto inbound_json = json::parse(input);
        InboundPackets inbound;
        for (auto& m : inbound_json["Messages"]) {
            inbound.Messages.push_back({
                m["Tag"].get<std::string>(),
                { m["Remote"]["IpAddress"].get<std::string>() }
                
            });
        }

        // Count packets per IP
        std::unordered_map<std::string, int> counts;
        for (auto& msg : inbound.Messages) {
            counts[msg.remote.IpAddress]++;
        }

        // Helper to get & clear count and base64 encode
        auto get_and_clear = [&](std::unordered_map<std::string,int>& m, const std::string& key) -> std::optional<std::string> {
            auto it = m.find(key);
            if (it == m.end() || it->second == 0) return std::nullopt;
            int value = it->second;
            it->second = 0;
            return base64_rfc4648::encode(std::to_string(value) + "\n");
        };

        // Build replies
        Response resp;
        for (auto& msg : inbound.Messages) {
            std::cout << msg.Tag << std::endl;
            auto data = get_and_clear(counts, msg.remote.IpAddress);
            if (!data.has_value()) continue;
            
            resp.Replies.push_back({
                current_utc_iso(),
                msg.Tag,
                data.value()
            });
        }

        // Output JSON response to Lambda
        json out_json;
        for (auto& r : resp.Replies) {
            out_json["Replies"].push_back({
                {"GeneratedAt", r.GeneratedAt},
                {"Tag", r.Tag},
                {"Data", r.Data}
            });
        }

        return invocation_response::success(out_json.dump(), "application/json");
    }
    catch(const std::exception& e)
    {
        std::cerr << "Exception in response creation: " << e.what() << std::endl;
        return invocation_response::failure(e.what(), "ResponseError");
    }
}

int main()
{
    run_handler(my_handler);
    return 0;
}
