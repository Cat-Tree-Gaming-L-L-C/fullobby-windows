use dioxus::prelude::*;

use crate::state::servers::ServerStats;

#[derive(Props, Clone, PartialEq)]
pub struct ServerStatsListProps {
    pub stats: Vec<ServerStats>,
    pub server_count: usize,
    pub seeding_threshold: Vec<i32>,
    #[props(default = false)]
    pub is_eu: bool,
    #[props(default = false)]
    pub show_real_max: bool,
}

#[component]
pub fn ServerStatsList(props: ServerStatsListProps) -> Element {
    // Skeleton loading state
    if props.stats.is_empty() && props.server_count > 0 {
        let skeleton_class = if props.is_eu {
            "badge badge-ghost mt-1 w-full skeleton h-6 opacity-80"
        } else {
            "badge badge-ghost mt-1 w-full skeleton h-6"
        };
        return rsx! {
            for i in 0..props.server_count {
                div {
                    key: "skeleton-{i}",
                    class: skeleton_class,
                }
            }
        };
    }

    rsx! {
        for (i, server) in props.stats.iter().enumerate() {
            div {
                key: "{server.name}",
                class: match (server.offline, props.is_eu) {
                    (true, true)   => "badge mt-1 w-full badge-error opacity-80",
                    (true, false)  => "badge mt-1 w-full badge-error",
                    (false, true)  => "badge mt-1 w-full badge-ghost opacity-80",
                    (false, false) => "badge mt-1 w-full badge-ghost",
                },
                {
                    let display_map = if server.offline { "Offline" } else { &server.map };
                    let max = if props.show_real_max {
                        server.max_player_count.unwrap_or(100)
                    } else {
                        props.seeding_threshold.get(i).copied().unwrap_or(75)
                    };
                    format!("{} - {} - {}/{}", server.name, display_map, server.players, max)
                }
            }
        }
    }
}
